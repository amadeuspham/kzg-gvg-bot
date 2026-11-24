using System.Text.Json;
using System.Text.Json.Serialization;
using Kzg.Core.Abstractions;
using Kzg.Protocol;

namespace Kzg.Core.Config;

// Discord IDs are decimal strings in the files as on the wire, in Snowflake's one canonical form:
// a JSON number would lose digits in any JavaScript-like tool that opens the file.

internal sealed class SnowflakeConverter : JsonConverter<ulong>
{
    public override ulong Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && Snowflake.TryParse(reader.GetString(), out var id) ? id : throw new JsonException();

    public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Snowflake.Format(value));
}

internal sealed class UserIdConverter : JsonConverter<UserId>
{
    public override UserId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && UserId.TryParse(reader.GetString(), out var id) ? id : throw new JsonException();

    public override void Write(Utf8JsonWriter writer, UserId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWire());
}

internal sealed class ChannelIdConverter : JsonConverter<ChannelId>
{
    public override ChannelId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && ChannelId.TryParse(reader.GetString(), out var id) ? id : throw new JsonException();

    public override void Write(Utf8JsonWriter writer, ChannelId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWire());
}
