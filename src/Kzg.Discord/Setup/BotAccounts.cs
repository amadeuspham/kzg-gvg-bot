using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kzg.Discord.Setup;

/// <summary>A bot account as Discord describes it to its own token: the application to invite, and its name.</summary>
/// <param name="BotUserId">The bot's own user, when Discord says; the bot learns it at sign-in otherwise.</param>
public sealed record BotAccount(ulong ApplicationId, ulong? BotUserId, string Name);

/// <summary>A Discord server a bot is in.</summary>
public sealed record ServerRef(ulong Id, string Name);

/// <summary>What Discord said of a token.</summary>
public abstract record TokenCheck
{
    private TokenCheck()
    {
    }

    public sealed record Accepted(BotAccount Account) : TokenCheck;

    /// <summary>Discord doesn't know the token: mistyped, reset since, or not a bot's.</summary>
    public sealed record Refused : TokenCheck;

    /// <summary>Discord couldn't be asked; the token may be fine.</summary>
    public sealed record Unreachable : TokenCheck;
}

/// <summary>What the setup page asks Discord about the bot tokens pasted into it. The tokens go nowhere else.</summary>
public interface IBotAccounts
{
    Task<TokenCheck> CheckAsync(string token, CancellationToken cancellationToken);

    /// <summary>The servers the bot is in; null when Discord couldn't be asked or refused the token.</summary>
    Task<IReadOnlyList<ServerRef>?> ServersAsync(string token, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IBotAccounts"/> over Discord's HTTP API, with the token in the <c>Authorization</c> header only.
/// Nothing it logs or throws carries the token.
/// </summary>
public sealed partial class DiscordBotAccounts(HttpClient http) : IBotAccounts
{
    public const string Api = "https://discord.com/api/v10";

    private const int MaxNameLength = 100;

    /// <summary>
    /// The token as Discord wants it, from what someone pasted: spaces and a leading "Bot " dropped. Null when
    /// it can't be a bot token, so a pasted client secret or URL is turned away before it is sent anywhere.
    /// </summary>
    public static string? Normalize(string? pasted)
    {
        var text = pasted?.Trim() ?? "";
        if (text.StartsWith("Bot ", StringComparison.OrdinalIgnoreCase))
        {
            text = text[4..].Trim();
        }
        return text.Length is >= 50 and <= 200 && TokenShape().IsMatch(text) ? text : null;
    }

    public async Task<TokenCheck> CheckAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            using var request = Request($"{Api}/applications/@me", token);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new TokenCheck.Refused();
            }
            if (!response.IsSuccessStatusCode)
            {
                return new TokenCheck.Unreachable();
            }
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), default, cancellationToken).ConfigureAwait(false);
            var root = body.RootElement;
            if (!Id(root, "id", out var application))
            {
                return new TokenCheck.Unreachable();
            }
            ulong? bot = root.TryGetProperty("bot", out var user) && user.ValueKind == JsonValueKind.Object && Id(user, "id", out var id) ? id : null;
            return new TokenCheck.Accepted(new BotAccount(application, bot, Name(root)));
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new TokenCheck.Unreachable();
        }
    }

    public async Task<IReadOnlyList<ServerRef>?> ServersAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            using var request = Request($"{Api}/users/@me/guilds", token);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), default, cancellationToken).ConfigureAwait(false);
            if (body.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            List<ServerRef> servers = [];
            foreach (var guild in body.RootElement.EnumerateArray())
            {
                if (guild.ValueKind == JsonValueKind.Object && Id(guild, "id", out var id))
                {
                    servers.Add(new ServerRef(id, Name(guild)));
                }
            }
            return servers;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static HttpRequestMessage Request(string url, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);
        return request;
    }

    private static bool Id(JsonElement element, string name, out ulong id)
    {
        id = 0;
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && Protocol.Snowflake.TryParse(value.GetString(), out id);
    }

    private static string Name(JsonElement element)
    {
        var name = element.TryGetProperty("name", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        return name.Length <= MaxNameLength ? name : name[..MaxNameLength];
    }

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenShape();
}
