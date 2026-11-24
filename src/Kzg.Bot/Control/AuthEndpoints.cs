using Kzg.Core.Pairing;

namespace Kzg.Bot.Control;

/// <summary>What one HTTP request gets back; the host writes it out as it is.</summary>
/// <param name="Location">Where a redirect goes; null for a page.</param>
public sealed record HttpReply(int Status, string ContentType, string Body, Uri? Location = null)
{
    /// <summary>
    /// Sent with every reply: these pages and redirects carry a <c>loginId</c>, so no cache keeps them
    /// and no referrer passes them on.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Headers { get; } = new Dictionary<string, string>
    {
        ["Cache-Control"] = "no-store",
        ["Referrer-Policy"] = "no-referrer",
        ["X-Content-Type-Options"] = "nosniff",
    };
}

/// <summary>"Sign in with Discord" as the bot serves it: the client ID and the host the browser comes back to.</summary>
public sealed record SignInSettings(ulong ClientId, string PublicHost);

/// <summary>
/// <c>GET /auth/start</c>, <c>GET /auth/callback</c> and <c>GET /healthz</c>, free of any web
/// framework: the host maps each route to one call. The pages say only fixed words, never anything
/// from the request, so nothing a link carries can land in them.
/// </summary>
/// <param name="settings">The sign-in settings under the current config; null while sign-in is off.</param>
public sealed class AuthEndpoints(PendingLogins logins, IDiscordIdentity discord, Func<SignInSettings?> settings)
{
    // Far longer than any code Discord issues; a longer one is not Discord's.
    private const int MaxCodeLength = 512;

    public static HttpReply Healthz() => new(200, "text/plain; charset=utf-8", "ok\n");

    /// <summary>The app opened the browser here: on to Discord's sign-in page, if an app waits on this login.</summary>
    public HttpReply Start(string? login)
    {
        if (settings() is not { } signIn)
        {
            return Page(404, "Discord sign-in is off", "Discord sign-in isn't turned on for this bot. Use /connect in Discord instead.");
        }
        if (SecretHash.OfWire(login) is not { } hash)
        {
            return Page(400, "Not a sign-in link", "This isn't a sign-in link. Start again from the app.");
        }
        return logins.CanStart(hash) switch
        {
            CallbackClaim.Granted => new HttpReply(302, "text/plain; charset=utf-8", "", DiscordAuthorize.Url(signIn.ClientId, signIn.PublicHost, login!)),
            CallbackClaim.Expired => Page(410, "Sign-in expired", "This sign-in link has expired. Start again from the app."),
            CallbackClaim.Used => Page(409, "Already signed in", "This sign-in link was already used. Start again from the app if it isn't linked."),
            _ => Page(404, "Sign-in not found", "The app isn't waiting for this sign-in. Start again from the app."),
        };
    }

    /// <summary>Discord sent the browser back: find out who signed in, and tell the waiting app.</summary>
    /// <param name="error">Discord's <c>error</c>, as when the player chose Cancel.</param>
    public async Task<HttpReply> CallbackAsync(string? code, string? state, string? error, CancellationToken cancellationToken)
    {
        if (settings() is not { } signIn)
        {
            return Page(404, "Discord sign-in is off", "Discord sign-in isn't turned on for this bot. Use /connect in Discord instead.");
        }
        if (SecretHash.OfWire(state) is not { } hash)
        {
            return Page(400, "Not a sign-in link", "This isn't a sign-in link. Start again from the app.");
        }
        if (error is not null)
        {
            // The login stays open: the player may try again from the same app.
            return Page(400, "Sign-in cancelled", "Sign-in was cancelled. Try again from the app.");
        }
        if (string.IsNullOrEmpty(code) || code.Length > MaxCodeLength)
        {
            return Page(400, "Not a sign-in link", "Discord's reply was incomplete. Try again from the app.");
        }
        switch (logins.BeginCallback(hash))
        {
            case CallbackClaim.Expired:
                return Page(410, "Sign-in expired", "This sign-in has expired. Start again from the app.");
            case CallbackClaim.Used:
                return Page(409, "Already signed in", "This sign-in was already used. Return to the app.");
            case CallbackClaim.Unknown:
                return Page(404, "Sign-in not found", "The app isn't waiting for this sign-in. Start again from the app.");
        }
        try
        {
            var user = await discord.ExchangeCodeAsync(code, DiscordAuthorize.RedirectUri(signIn.PublicHost), cancellationToken).ConfigureAwait(false);
            if (user is not { } signedIn)
            {
                logins.Release(hash);
                return Page(400, "Sign-in failed", "Discord didn't accept this sign-in. Try again from the app.");
            }
            var member = await discord.GetMemberAsync(signedIn, cancellationToken).ConfigureAwait(false);
            if (member is null)
            {
                logins.Complete(hash, new SignInOutcome.NotMember());
                return Page(403, "Not a member", "That Discord account isn't a member of the guild's server, so it can't be linked.");
            }
            logins.Complete(hash, new SignInOutcome.Member(member));
            return Page(200, "Signed in", "Signed in, return to the app.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logins.Release(hash);
            return Page(502, "Discord unreachable", "The bot couldn't reach Discord. Try again from the app.");
        }
        catch (OperationCanceledException)
        {
            logins.Release(hash);
            throw;
        }
    }

    private static HttpReply Page(int status, string title, string text) =>
        new(status, "text/html; charset=utf-8",
            $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>{title}</title></head>"
            + $"<body><p>{text}</p></body></html>\n");
}
