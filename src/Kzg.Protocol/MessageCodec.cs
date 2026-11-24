using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.Unicode;

namespace Kzg.Protocol;

/// <summary>What reading one message gave.</summary>
public abstract record MessageParse<TMessage>
    where TMessage : KzgMessage
{
    private MessageParse()
    {
    }

    public sealed record Parsed(TMessage Message) : MessageParse<TMessage>;

    /// <summary>Longer than <see cref="WireProtocol.MaxMessageBytes"/>; nothing of it was read.</summary>
    public sealed record TooLarge(int Bytes) : MessageParse<TMessage>;

    /// <summary>A well-formed envelope with a <c>v</c> this build doesn't speak.</summary>
    public sealed record UnsupportedVersion(int Version) : MessageParse<TMessage>;

    /// <summary>
    /// Not JSON, not a known type for this direction, a field missing, extra, repeated or of the wrong
    /// kind, or a rule broken. <paramref name="Reason"/> names fields only, never their values.
    /// </summary>
    public sealed record Malformed(string Reason) : MessageParse<TMessage>;
}

/// <summary>
/// Reads and writes <c>/ws</c> messages. Reading is strict: one message per call, at most
/// <see cref="WireProtocol.MaxMessageBytes"/>, a known type for its direction, every field the type
/// requires and no other, none twice, and the type's own rules. Writing checks the same rules, so
/// whatever one side writes, the other reads.
/// </summary>
public static class MessageCodec
{
    private static readonly WireJsonContext Json = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Names in other scripts stay as UTF-8, not six-byte escapes that eat the size cap; never HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 4,
    });

    private static readonly Dictionary<string, JsonTypeInfo> ClientTypes = new(StringComparer.Ordinal)
    {
        [HelloMessage.TypeName] = Json.HelloMessage,
        [PressMessage.TypeName] = Json.PressMessage,
        [OpenMicMessage.TypeName] = Json.OpenMicMessage,
        [HeartbeatMessage.TypeName] = Json.HeartbeatMessage,
        [UnlinkMessage.TypeName] = Json.UnlinkMessage,
    };

    private static readonly Dictionary<string, JsonTypeInfo> ServerTypes = new(StringComparer.Ordinal)
    {
        [WelcomeMessage.TypeName] = Json.WelcomeMessage,
        [StatusMessage.TypeName] = Json.StatusMessage,
        [HeartbeatAckMessage.TypeName] = Json.HeartbeatAckMessage,
        [ErrorMessage.TypeName] = Json.ErrorMessage,
    };

    /// <summary>Reads what the app sent; the bot calls this.</summary>
    public static MessageParse<ClientMessage> ParseClient(ReadOnlySpan<byte> utf8) => Parse<ClientMessage>(utf8, ClientTypes);

    /// <summary>Reads what the bot sent; the app calls this.</summary>
    public static MessageParse<ServerMessage> ParseServer(ReadOnlySpan<byte> utf8) => Parse<ServerMessage>(utf8, ServerTypes);

    /// <summary>
    /// The message as UTF-8 JSON. Throws <see cref="ArgumentException"/> for a message that breaks its
    /// rules, and <see cref="InvalidOperationException"/> when it would exceed the size cap.
    /// </summary>
    public static byte[] Serialize(KzgMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Problem() is { } problem)
        {
            throw new ArgumentException(problem, nameof(message));
        }
        var types = message is ClientMessage ? ClientTypes : ServerTypes;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, types[message.Type]);
        return bytes.Length <= WireProtocol.MaxMessageBytes
            ? bytes
            : throw new InvalidOperationException(
                $"a {message.Type} message is {bytes.Length} bytes; the cap is {WireProtocol.MaxMessageBytes}");
    }

    private static MessageParse<TMessage> Parse<TMessage>(ReadOnlySpan<byte> utf8, Dictionary<string, JsonTypeInfo> types)
        where TMessage : KzgMessage
    {
        if (utf8.Length > WireProtocol.MaxMessageBytes)
        {
            return new MessageParse<TMessage>.TooLarge(utf8.Length);
        }
        if (!Utf8.IsValid(utf8))
        {
            return new MessageParse<TMessage>.Malformed("not UTF-8");
        }
        if (ReadEnvelope(utf8, out var version, out var type) is { } envelopeProblem)
        {
            return new MessageParse<TMessage>.Malformed(envelopeProblem);
        }
        if (version != WireProtocol.Version)
        {
            return new MessageParse<TMessage>.UnsupportedVersion(version);
        }
        if (!types.TryGetValue(type, out var typeInfo))
        {
            return new MessageParse<TMessage>.Malformed("type is not a message this side receives");
        }
        TMessage message;
        try
        {
            message = (TMessage)JsonSerializer.Deserialize(utf8, typeInfo)!;
        }
        catch (JsonException e)
        {
            // The exception's own text can quote the input; the path names only where it went wrong.
            return new MessageParse<TMessage>.Malformed($"{type} does not match its fields at {e.Path ?? "$"}");
        }
        return message.Problem() is { } problem
            ? new MessageParse<TMessage>.Malformed(problem)
            : new MessageParse<TMessage>.Parsed(message);
    }

    // A first pass over the top level only: the whole text must be one JSON object holding an integer
    // v and a string type, each once, before the version and type decide how the rest is read.
    private static string? ReadEnvelope(ReadOnlySpan<byte> utf8, out int version, out string type)
    {
        version = 0;
        type = "";
        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = 4 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return "a message is a JSON object";
            }
            bool sawVersion = false, sawType = false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isVersion = reader.ValueTextEquals("v"u8);
                var isType = reader.ValueTextEquals("type"u8);
                reader.Read();
                if (isVersion)
                {
                    if (sawVersion || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out version))
                    {
                        return "v must be one integer";
                    }
                    sawVersion = true;
                }
                else if (isType)
                {
                    if (sawType || reader.TokenType != JsonTokenType.String)
                    {
                        return "type must be one string";
                    }
                    type = reader.GetString()!;
                    sawType = true;
                }
                reader.Skip();
            }
            if (reader.Read())
            {
                return "a message is one JSON object and nothing after it";
            }
            return !sawVersion ? "v is missing" : !sawType ? "type is missing" : null;
        }
        catch (JsonException)
        {
            return "not valid JSON";
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HelloMessage))]
[JsonSerializable(typeof(PressMessage))]
[JsonSerializable(typeof(OpenMicMessage))]
[JsonSerializable(typeof(HeartbeatMessage))]
[JsonSerializable(typeof(UnlinkMessage))]
[JsonSerializable(typeof(WelcomeMessage))]
[JsonSerializable(typeof(StatusMessage))]
[JsonSerializable(typeof(HeartbeatAckMessage))]
[JsonSerializable(typeof(ErrorMessage))]
internal sealed partial class WireJsonContext : JsonSerializerContext;
