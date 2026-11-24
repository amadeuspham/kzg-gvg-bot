using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Kzg.Core.Abstractions;
using Kzg.Core.Pairing;
using Microsoft.Extensions.Logging;
using NetCord.Gateway;
using NetCord.Rest;

namespace Kzg.Discord.Identity;

/// <summary>The Discord application's sign-in credentials. The secret never leaves this process and is never logged.</summary>
public sealed class OAuthCredentials(ulong clientId, string clientSecret)
{
    public ulong ClientId { get; } = clientId;

    internal string Secret { get; } = clientSecret;

    public override string ToString() => $"client {ClientId}";
}

/// <summary>A guild member's names, as Discord gives them.</summary>
public sealed record MemberNames(string? Nickname, string? GlobalName, string? Username);

/// <summary>Looks a user up in the guild.</summary>
public interface IGuildDirectory
{
    /// <summary>The member's names; null when the user isn't in the guild. Throws when Discord can't be reached.</summary>
    Task<MemberNames?> FindAsync(ulong guildId, ulong userId, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IDiscordIdentity"/> over Discord's HTTP API: "Sign in with Discord" trades the code for the user
/// with the client secret, and guild membership comes from the bot's own token. The code, the access token
/// and the secret appear nowhere but in the requests to Discord.
/// </summary>
public sealed class DiscordIdentity(
    HttpClient http,
    Func<OAuthCredentials?> credentials,
    Func<ulong?> guildId,
    IGuildDirectory directory,
    ILogger? log = null) : IDiscordIdentity
{
    public const string Api = "https://discord.com/api/v10";

    private const int MaxNameLength = 100;

    public async Task<UserId?> ExchangeCodeAsync(string code, Uri redirectUri, CancellationToken cancellationToken)
    {
        if (credentials() is not { } oauth)
        {
            return null;
        }
        using var token = new HttpRequestMessage(HttpMethod.Post, $"{Api}/oauth2/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri.AbsoluteUri,
            }),
        };
        token.Headers.Authorization = Basic(oauth);
        string accessToken;
        using (var response = await http.SendAsync(token, cancellationToken).ConfigureAwait(false))
        {
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                // invalid_grant: the code is wrong, used or expired, or the redirect doesn't match.
                return null;
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized)
            {
                log?.LogError("Discord refused the bot's client credentials; check oauth.clientId and the client secret");
                throw new InvalidOperationException("Discord refused the bot's client credentials");
            }
            response.EnsureSuccessStatusCode();
            using var body = await Parse(response, cancellationToken).ConfigureAwait(false);
            accessToken = body.RootElement.TryGetProperty("access_token", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()! : throw new InvalidOperationException("Discord's token reply had no access token");
        }
        try
        {
            return await WhoAsync(accessToken, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await RevokeAsync(accessToken, oauth).ConfigureAwait(false);
        }
    }

    public async Task<GuildMember?> GetMemberAsync(UserId user, CancellationToken cancellationToken)
    {
        if (guildId() is not { } guild)
        {
            throw new InvalidOperationException("the bot hasn't settled on a server yet");
        }
        var names = await directory.FindAsync(guild, user.V, cancellationToken).ConfigureAwait(false);
        return names is null ? null : new GuildMember(user, DisplayName(names, user));
    }

    /// <summary>The name a player is shown by: nickname, then display name, then account name; tidied to what the wire takes.</summary>
    public static string DisplayName(MemberNames names, UserId user)
    {
        foreach (var candidate in new[] { names.Nickname, names.GlobalName, names.Username })
        {
            var name = Tidy(candidate);
            if (name.Length > 0)
            {
                return name;
            }
        }
        return $"Player {user}";
    }

    private async Task<UserId?> WhoAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var me = new HttpRequestMessage(HttpMethod.Get, $"{Api}/users/@me");
        me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(me, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        using var body = await Parse(response, cancellationToken).ConfigureAwait(false);
        return body.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && UserId.TryParse(id.GetString(), out var user)
            ? user : null;
    }

    /// <summary>The access token isn't needed once we know who signed in, so Discord is told to forget it; best effort.</summary>
    private async Task RevokeAsync(string accessToken, OAuthCredentials oauth)
    {
        try
        {
            using var revoke = new HttpRequestMessage(HttpMethod.Post, $"{Api}/oauth2/token/revoke")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = accessToken, ["token_type_hint"] = "access_token" }),
            };
            revoke.Headers.Authorization = Basic(oauth);
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var _ = await http.SendAsync(revoke, cancel.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            log?.LogDebug("revoking a sign-in token failed: {Error}", exception.GetType().Name);
        }
    }

    private static AuthenticationHeaderValue Basic(OAuthCredentials oauth) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{oauth.ClientId}:{oauth.Secret}")));

    private static async Task<JsonDocument> Parse(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string Tidy(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }
        var text = new string([.. name.Where(c => !char.IsControl(c))]).Trim();
        if (text.Length > MaxNameLength)
        {
            text = text[..MaxNameLength];
            // Not in the middle of a surrogate pair.
            if (char.IsHighSurrogate(text[^1]))
            {
                text = text[..^1];
            }
        }
        return text;
    }
}

/// <summary>The guild's members through the bot's REST client.</summary>
internal sealed class NetCordGuildDirectory(Func<RestClient?> rest) : IGuildDirectory
{
    public async Task<MemberNames?> FindAsync(ulong guildId, ulong userId, CancellationToken cancellationToken)
    {
        var client = rest() ?? throw new InvalidOperationException("the bot isn't signed in yet");
        try
        {
            var member = await client.GetGuildUserAsync(guildId, userId, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new MemberNames(member.Nickname, member.GlobalName, member.Username);
        }
        catch (RestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}
