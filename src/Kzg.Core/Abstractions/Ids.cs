using System.Globalization;
using Kzg.Protocol;

namespace Kzg.Core.Abstractions;

/// <summary>A Discord user. On the wire it is a snowflake string; see <see cref="Snowflake"/>.</summary>
public readonly record struct UserId(ulong V)
{
    public static bool TryParse(string? text, out UserId id)
    {
        var ok = Snowflake.TryParse(text, out var value);
        id = new UserId(value);
        return ok;
    }

    public override string ToString() => V.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A Discord voice channel. On the wire it is a snowflake string.</summary>
public readonly record struct ChannelId(ulong V)
{
    public static bool TryParse(string? text, out ChannelId id)
    {
        var ok = Snowflake.TryParse(text, out var value);
        id = new ChannelId(value);
        return ok;
    }

    public override string ToString() => V.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Where relayed audio comes from: <c>user:&lt;id&gt;</c> for a player, <c>tts:&lt;name&gt;</c> for a
/// synthetic source such as phase 2's jungle calls. Routing sessions are keyed by it.
/// </summary>
public readonly record struct SourceId(string V)
{
    private const string UserPrefix = "user:";
    private const string TtsPrefix = "tts:";

    public static SourceId ForUser(UserId user) => new(UserPrefix + Snowflake.Format(user.V));

    public static SourceId ForTts(string name) =>
        string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("a synthetic source needs a name", nameof(name))
        : new(TtsPrefix + name);

    /// <summary>The player behind a <c>user:</c> source; false for a synthetic one.</summary>
    public bool TryGetUser(out UserId user)
    {
        user = default;
        return V is not null && V.StartsWith(UserPrefix, StringComparison.Ordinal)
            && UserId.TryParse(V[UserPrefix.Length..], out user);
    }

    public override string ToString() => V;
}
