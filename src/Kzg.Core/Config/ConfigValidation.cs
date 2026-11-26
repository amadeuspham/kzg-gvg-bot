using System.Globalization;
using System.Net;
using Kzg.Core.Abstractions;
using Kzg.Protocol;
using Group = Kzg.Core.Abstractions.Group;

namespace Kzg.Core.Config;

/// <summary>
/// The rules a <see cref="BotConfig"/> keeps beyond its JSON types. Loading and saving both apply
/// them, so the bot never runs on, and a command never writes, a config that breaks one.
/// </summary>
public static class ConfigValidation
{
    public const int MaxPrerollMs = 1000;

    /// <summary>Every error, in the file's order; empty when the config keeps every rule.</summary>
    /// <param name="secrets">Where each bot's <see cref="BotEntry.TokenRef"/> and the client secret must resolve.</param>
    /// <param name="checkSecrets">False for the setup page, which reads an earlier config to keep its settings while the tokens are pasted afresh.</param>
    public static IReadOnlyList<ConfigError> Validate(BotConfig config, Secrets secrets, bool checkSecrets = true)
    {
        List<ConfigError> errors = [];
        void Error(string setting, string problem) => errors.Add(new ConfigError(null, setting, problem));

        if (config.SchemaVersion != BotConfig.CurrentSchemaVersion)
        {
            Error("schemaVersion", $"this build reads schema version {BotConfig.CurrentSchemaVersion} only");
        }
        if (config.PublicHost is not null && !ConnectCode.IsValidHost(config.PublicHost))
        {
            Error("publicHost", "a DNS name, IPv4 or [IPv6] address, optionally with :port, and nothing else");
        }
        if (!IsListenAddress(config.Listen))
        {
            Error("listen", "an IP address and a port, such as 127.0.0.1:7878");
        }
        for (var i = 0; i < config.TrustedProxies.Count; i++)
        {
            if (!TryParseNetwork(config.TrustedProxies[i], out _))
            {
                Error($"trustedProxies[{i}]", "an IP address or a CIDR network such as 10.0.0.0/8, with no bits set past its prefix");
            }
        }
        // The pre-roll, the 60 ms catch-up allowance and the longest playout delay fit the 1.2 s buffer.
        if (config.PrerollMs is < 0 or > MaxPrerollMs || config.PrerollMs % OpusFormat.FrameMs != 0)
        {
            Error("prerollMs", $"0 to {MaxPrerollMs}, in whole {OpusFormat.FrameMs} ms frames");
        }
        if (config.MaxHoldMs is < 1_000 or > 600_000)
        {
            Error("maxHoldMs", "1000 to 600000");
        }
        if (config.DeviceTokenDays is < 1 or > 3650)
        {
            Error("deviceTokenDays", "1 to 3650");
        }
        if (config.JungleVolume is < 1 or > 100)
        {
            Error("jungleVolume", "1 to 100");
        }
        if (config.OAuth.Enabled && config.OAuth.ClientId is null)
        {
            Error("oauth.clientId", "Discord sign-in needs the application's client ID");
        }
        if (checkSecrets && config.OAuth.Enabled && !secrets.Has(Secrets.OAuthClientSecretName))
        {
            Error("oauth.enabled", $"Discord sign-in needs {Secrets.OAuthClientSecretName} in secrets.json");
        }
        ValidateGroups(config.Groups, Error);
        ValidateBots(config.Bots, checkSecrets ? secrets : null, Error);
        return errors;
    }

    /// <summary>A <c>trustedProxies</c> entry: one address, or a network with nothing set past its prefix.</summary>
    public static bool TryParseNetwork(string? text, out IPNetwork network)
    {
        network = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (!IsPlainAddress(slash < 0 ? text : text[..slash], out var address))
        {
            return false;
        }
        if (slash < 0)
        {
            network = new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32);
            return true;
        }
        // IPNetwork would quietly clear bits past the prefix; a typo such as 10.0.0.1/8 is refused instead.
        return text[(slash + 1)..].All(char.IsAsciiDigit) && IPNetwork.TryParse(text, out network) && network.BaseAddress.Equals(address);
    }

    // IPAddress also reads "127.1" and "10" as IPv4, and IPv6 with a zone; a list of networks wants neither.
    private static bool IsPlainAddress(string text, out IPAddress address) =>
        IPAddress.TryParse(text, out address!)
        && (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? address.ScopeId == 0 && !text.Contains('%', StringComparison.Ordinal)
            : text.Count(c => c == '.') == 3);

    private static bool IsListenAddress(string text) =>
        IPEndPoint.TryParse(text, out var endPoint) && endPoint.Port != 0
        // TryParse also takes a bare address, as port 0, and an IPv6 address without brackets.
        && text.EndsWith(":" + endPoint.Port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
        && (endPoint.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6 || text.StartsWith('['));

    private static void ValidateGroups(GroupsConfig groups, Action<string, string> error)
    {
        HashSet<ChannelId> bound = [];
        foreach (var (group, name) in new[] { (Group.Attack, "attack"), (Group.Defense, "defense") })
        {
            var channels = groups[group].Channels;
            for (var i = 0; i < channels.Count; i++)
            {
                if (!bound.Add(channels[i]))
                {
                    error($"groups.{name}.channels[{i}]", "a channel is bound once, to one group");
                }
            }
        }
    }

    private static void ValidateBots(IReadOnlyList<BotEntry> bots, Secrets? secrets, Action<string, string> error)
    {
        HashSet<string> refs = new(StringComparer.Ordinal);
        HashSet<UserId> users = [];
        HashSet<ChannelId> channels = [];
        var primaries = 0;
        for (var i = 0; i < bots.Count; i++)
        {
            // The reader lets a null into a list whatever the list's type says.
            if (bots[i] is not { } bot)
            {
                error($"bots[{i}]", "a bot, not null");
                continue;
            }
            if (!Secrets.IsValidName(bot.TokenRef))
            {
                error($"bots[{i}].tokenRef", "1 to 64 letters, digits or underscores");
            }
            else if (!refs.Add(bot.TokenRef))
            {
                error($"bots[{i}].tokenRef", "each bot has a token of its own");
            }
            else if (secrets is not null && !secrets.Has(bot.TokenRef))
            {
                error($"bots[{i}].tokenRef", "no secret of this name in secrets.json or the environment");
            }
            if (bot.BotUserId is { } user && !users.Add(user))
            {
                error($"bots[{i}].botUserId", "each bot account is listed once");
            }
            if (bot.ChannelId is { } channel && !channels.Add(channel))
            {
                error($"bots[{i}].channelId", "a channel has one bot at most, or its relay would be heard twice");
            }
            primaries += bot.Primary ? 1 : 0;
        }
        if (primaries != 1)
        {
            error("bots", "exactly one bot is primary");
        }
    }
}
