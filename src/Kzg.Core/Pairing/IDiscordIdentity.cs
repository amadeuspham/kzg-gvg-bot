using System.Globalization;
using Kzg.Core.Abstractions;

namespace Kzg.Core.Pairing;

/// <summary>A member of the guild, as the welcome names them.</summary>
public sealed record GuildMember(UserId User, string DisplayName);

/// <summary>
/// What pairing asks of Discord. The Discord adapter implements it with the bot's token and the OAuth
/// client secret, which never leave the server; tests use a fake.
/// </summary>
public interface IDiscordIdentity
{
    /// <summary>
    /// Trades the code Discord's sign-in page returned for the user who signed in, asking for the
    /// <c>identify</c> scope only. Null when Discord refuses the code; throws when Discord can't be reached.
    /// </summary>
    Task<UserId?> ExchangeCodeAsync(string code, Uri redirectUri, CancellationToken cancellationToken);

    /// <summary>The user as a member of the guild; null when they aren't one. Throws when Discord can't be reached.</summary>
    Task<GuildMember?> GetMemberAsync(UserId user, CancellationToken cancellationToken);
}

/// <summary>Where "Sign in with Discord" sends the browser, and where Discord sends it back.</summary>
public static class DiscordAuthorize
{
    public const string AuthorizeEndpoint = "https://discord.com/oauth2/authorize";

    /// <summary>The bot's own callback page on its public host.</summary>
    public static Uri RedirectUri(string publicHost) => new($"https://{publicHost}/auth/callback");

    /// <summary>Discord's sign-in page for this login: the <c>identify</c> scope only, and the <c>loginId</c> as <c>state</c>.</summary>
    public static Uri Url(ulong clientId, string publicHost, string state) =>
        new(AuthorizeEndpoint
            + "?response_type=code"
            + "&client_id=" + clientId.ToString(CultureInfo.InvariantCulture)
            + "&scope=identify"
            + "&state=" + Uri.EscapeDataString(state)
            + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri(publicHost).AbsoluteUri)
            + "&prompt=none");
}
