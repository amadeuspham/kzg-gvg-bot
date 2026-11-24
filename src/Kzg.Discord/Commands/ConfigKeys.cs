using System.Globalization;
using Kzg.Core.Abstractions;
using Kzg.Core.Config;

namespace Kzg.Discord.Commands;

/// <summary>
/// The settings <c>/gvg config set</c> can change, by the name <c>config.json</c> uses. Anything not listed
/// (the listen address, bots, groups) has a command of its own or is edited in the file. A value that
/// doesn't parse is refused here; one that parses but breaks a rule is refused by the config's own checks.
/// </summary>
public static class ConfigKeys
{
    /// <summary>What setting a key does.</summary>
    /// <param name="NeedsRestart">The running bot doesn't re-read it, so it applies after a restart.</param>
    public sealed record Key(string Name, string Help, bool NeedsRestart, Func<BotConfig, string, (BotConfig? Config, string? Problem)> Set, Func<BotConfig, string> Get);

    private const string None = "none";

    public static IReadOnlyList<Key> All { get; } =
    [
        Text("publicHost", "The address players' apps connect to, such as gvg.example.com", false, true, c => c.PublicHost, (c, v) => c with { PublicHost = v }),
        Id("adminRoleId", "A role whose members may run /gvg beside those with Manage Server", false, c => c.AdminRoleId, (c, v) => c with { AdminRoleId = v }),
        Int("prerollMs", "How far back a relay starts, in ms", false, c => c.PrerollMs, (c, v) => c with { PrerollMs = v }),
        Int("maxHoldMs", "The longest a key hold relays, in ms", false, c => c.MaxHoldMs, (c, v) => c with { MaxHoldMs = v }),
        Int("deviceTokenDays", "Days an unused device token lasts", true, c => c.DeviceTokenDays, (c, v) => c with { DeviceTokenDays = v }),
        Int("jungleVolume", "How loud jungle calls are, 1 to 100 (100 is the loudest)", false, c => c.JungleVolume, (c, v) => c with { JungleVolume = v }),
        Flag("oauth.enabled", "Sign in with Discord", false, c => c.OAuth.Enabled, (c, v) => c with { OAuth = c.OAuth with { Enabled = v } }),
        Id("oauth.clientId", "The Discord application's client ID", false, c => c.OAuth.ClientId, (c, v) => c with { OAuth = c.OAuth with { ClientId = v } }),
    ];

    public static Key? Find(string? name) => All.FirstOrDefault(key => string.Equals(key.Name, name, StringComparison.Ordinal));

    private static Key Text(string name, string help, bool restart, bool clearable, Func<BotConfig, string?> get, Func<BotConfig, string?, BotConfig> set) =>
        new(name, help, restart, (config, value) =>
            value.Trim() is { Length: > 0 } text
                ? (set(config, clearable && string.Equals(text, None, StringComparison.OrdinalIgnoreCase) ? null : text), null)
                : (null, $"{name} needs a value" + (clearable ? ", or 'none' to clear it" : "")), config => get(config) ?? "(not set)");

    private static Key Int(string name, string help, bool restart, Func<BotConfig, int> get, Func<BotConfig, int, BotConfig> set) =>
        new(name, help, restart, (config, value) =>
            int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? (set(config, number), null)
                : (null, $"{name} is a whole number"), config => get(config).ToString(CultureInfo.InvariantCulture));

    private static Key Flag(string name, string help, bool restart, Func<BotConfig, bool> get, Func<BotConfig, bool, BotConfig> set) =>
        new(name, help, restart, (config, value) => value.Trim().ToLowerInvariant() switch
        {
            "true" or "on" or "yes" => (set(config, true), null),
            "false" or "off" or "no" => (set(config, false), null),
            _ => ((BotConfig?)null, $"{name} is true or false"),
        }, config => get(config) ? "true" : "false");

    private static Key Id(string name, string help, bool restart, Func<BotConfig, ulong?> get, Func<BotConfig, ulong?, BotConfig> set) =>
        new(name, help, restart, (config, value) =>
        {
            var text = value.Trim();
            if (string.Equals(text, None, StringComparison.OrdinalIgnoreCase))
            {
                return (set(config, null), null);
            }
            return UserId.TryParse(text, out var id) ? (set(config, id.V), null) : (null, $"{name} is a Discord ID (digits), or 'none'");
        }, config => get(config)?.ToString(CultureInfo.InvariantCulture) ?? "(not set)");
}
