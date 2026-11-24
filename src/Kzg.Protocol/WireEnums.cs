using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kzg.Protocol;

/// <summary>A big team, as the wire spells it: <c>"attack"</c> or <c>"defense"</c>.</summary>
[JsonConverter(typeof(WireGroupConverter))]
public enum WireGroup
{
    Attack,
    Defense,
}

/// <summary>A broadcast action, as the wire spells it: <c>"group"</c>, <c>"allTeams"</c> or <c>"openMic"</c>.</summary>
[JsonConverter(typeof(WireActionConverter))]
public enum WireAction
{
    Group,
    AllTeams,
    OpenMic,
}

/// <summary>A warning the app shows under its status badge.</summary>
[JsonConverter(typeof(StatusWarningConverter))]
public enum StatusWarning
{
    NotInTeamVc,
    BotDown,
}

/// <summary>Why the bot refused the app.</summary>
[JsonConverter(typeof(ErrorCodeConverter))]
public enum ErrorCode
{
    BadCode,
    Expired,
    Used,
    Revoked,
    NotMember,
    VersionMismatch,
}

/// <summary>
/// Reads and writes an enum as exactly one of its wire names. Numbers, other casing and unknown names
/// are refused, where the built-in string converter would accept them.
/// </summary>
public abstract class WireEnumConverter<TEnum>(params (TEnum Value, string Name)[] names) : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private readonly Dictionary<string, TEnum> _byName = names.ToDictionary(n => n.Name, n => n.Value, StringComparer.Ordinal);
    private readonly Dictionary<TEnum, string> _byValue = names.ToDictionary(n => n.Value, n => n.Name);

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"expected a {typeof(TEnum).Name} name");
        }
        var name = reader.GetString()!;
        return _byName.TryGetValue(name, out var value)
            ? value
            : throw new JsonException($"not a {typeof(TEnum).Name} name");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        if (!_byValue.TryGetValue(value, out var name))
        {
            throw new JsonException($"{typeof(TEnum).Name} has no wire name for {value}");
        }
        writer.WriteStringValue(name);
    }

    /// <summary>The wire name of a value; throws for a value outside the enum.</summary>
    public string NameOf(TEnum value) =>
        _byValue.TryGetValue(value, out var name) ? name : throw new ArgumentOutOfRangeException(nameof(value));
}

public sealed class WireGroupConverter() : WireEnumConverter<WireGroup>(
    (WireGroup.Attack, "attack"),
    (WireGroup.Defense, "defense"));

public sealed class WireActionConverter() : WireEnumConverter<WireAction>(
    (WireAction.Group, "group"),
    (WireAction.AllTeams, "allTeams"),
    (WireAction.OpenMic, "openMic"));

public sealed class StatusWarningConverter() : WireEnumConverter<StatusWarning>(
    (StatusWarning.NotInTeamVc, "notInTeamVc"),
    (StatusWarning.BotDown, "botDown"));

public sealed class ErrorCodeConverter() : WireEnumConverter<ErrorCode>(
    (ErrorCode.BadCode, "badCode"),
    (ErrorCode.Expired, "expired"),
    (ErrorCode.Used, "used"),
    (ErrorCode.Revoked, "revoked"),
    (ErrorCode.NotMember, "notMember"),
    (ErrorCode.VersionMismatch, "versionMismatch"));
