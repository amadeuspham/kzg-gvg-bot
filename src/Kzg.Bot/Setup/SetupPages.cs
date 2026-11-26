using System.Globalization;
using System.Net;
using System.Text;
using Kzg.Core.Config;
using Kzg.Discord.Setup;

namespace Kzg.Bot.Setup;

/// <summary>
/// The setup page's HTML: plain forms, no script, so it works in any browser and a strict content policy
/// holds. Every word from Discord (bot and server names) is HTML-encoded; no token is ever written into a page.
/// </summary>
public static class SetupPages
{
    /// <summary>
    /// View Channel, Connect, Speak and Move Members: what a relay bot needs in voice. Change Nickname lets it
    /// show its team name, such as A1 Broadcaster; without it the bot relays all the same.
    /// </summary>
    public const ulong RelayPermissions = 1024 + 1_048_576 + 2_097_152 + 16_777_216 + 67_108_864;

    /// <summary>The relay's, plus Send Messages, so the first bot can post problems to the alerts channel.</summary>
    public const ulong PrimaryPermissions = RelayPermissions + 2048;

    /// <summary>The link that adds a bot to a server: the first bot also gets the slash commands.</summary>
    /// <param name="server">The server to offer, without a choice, once the first bot is in it.</param>
    public static Uri InviteLink(ulong applicationId, bool primary, ulong? server)
    {
        var url = new StringBuilder("https://discord.com/oauth2/authorize?client_id=")
            .Append(applicationId.ToString(CultureInfo.InvariantCulture))
            .Append(primary ? "&scope=bot%20applications.commands" : "&scope=bot")
            .Append("&permissions=").Append((primary ? PrimaryPermissions : RelayPermissions).ToString(CultureInfo.InvariantCulture));
        if (server is { } id)
        {
            url.Append("&guild_id=").Append(id.ToString(CultureInfo.InvariantCulture)).Append("&disable_guild_select=true");
        }
        return new Uri(url.ToString());
    }

    public static string Render(SetupView view) => view.Step switch
    {
        SetupStep.Tokens => Tokens(view),
        SetupStep.Invite => Invite(view),
        _ => Done(),
    };

    public static string Message(string title, string text) =>
        Page(title, $"<h2>{E(title)}</h2><p>{E(text)}</p>");

    private static string Tokens(SetupView view)
    {
        var body = new StringBuilder();
        body.Append("<h2>Step 1 of 3: paste the bot tokens</h2>")
            .Append("<p>The bot uses one Discord bot account for each team voice channel: up to six, A1 to A3 for the Attack channels and D1 to D3 for the Defense channels. Each field below is the bot for that channel, and the bot names itself after it in your server.</p>")
            .Append("<p>You can start with fewer and add more later: leave the fields of channels you don't use empty. If you later pick a channel in <code>/gvg setup</code> whose bot you left out, a bot whose channel you didn't pick fills in and takes that channel's name.</p>")
            .Append("<details open><summary>How to make a bot account</summary><ol>")
            .Append("<li>Open the <a href=\"https://discord.com/developers/applications\" target=\"_blank\" rel=\"noopener noreferrer\">Discord Developer Portal</a> and sign in.</li>")
            .Append("<li>Choose <b>New Application</b>, name it <i>Broadcast Relay</i>, and accept the terms.</li>")
            .Append("<li>Open the <b>Installation</b> tab, set <b>Install Link</b> to <b>None</b>, and save.</li>")
            .Append("<li>Open the <b>Bot</b> tab, turn <b>Public Bot</b> off, choose <b>Reset Token</b>, and copy the token.</li>")
            .Append("<li>Paste it below, and do the same for each bot.</li>")
            .Append("</ol><p>A token is the bot's password. Paste it here only, never into a chat or a screenshot. This page keeps it on this computer.</p></details>");
        AppendProblem(body, view.Problem);
        body.Append("<form method=\"post\" action=\"/setup/tokens\">");
        for (var i = 0; i < SetupWizard.MaxBots; i++)
        {
            var name = RelayNames.ForSlot(i + 1);
            var label = i == 0 ? $"{name} (this one also answers the /gvg and /connect commands)" : name;
            body.Append(CultureInfo.InvariantCulture, $"<label for=\"token{i + 1}\">{E(label)}</label>")
                .Append(CultureInfo.InvariantCulture, $"<input id=\"token{i + 1}\" name=\"token{i + 1}\" type=\"password\" autocomplete=\"off\" spellcheck=\"false\"{(i == 0 ? " required" : "")}>");
            if (view.FieldErrors.ElementAtOrDefault(i) is { } error)
            {
                body.Append("<p class=\"error\" role=\"alert\">").Append(E(error)).Append("</p>");
            }
        }
        body.Append("<label for=\"publicHost\">Public address (optional)</label>")
            .Append("<input id=\"publicHost\" name=\"publicHost\" type=\"text\" spellcheck=\"false\" placeholder=\"gvg.example.com\" value=\"").Append(E(view.PublicHost ?? "")).Append("\">")
            .Append("<p class=\"hint\">The web address players' apps connect to, through your HTTPS proxy. Leave it empty if you don't know it yet; <code>/gvg config set publicHost</code> sets it later.</p>")
            .Append("<button type=\"submit\">Check the tokens</button></form>");
        return Page("Paste the bot tokens", body.ToString());
    }

    private static string Invite(SetupView view)
    {
        var primaryServers = view.Bots.FirstOrDefault()?.Servers;
        ulong? home = primaryServers is { Count: 1 } ? primaryServers[0].Id : null;
        var body = new StringBuilder();
        body.Append("<h2>Step 2 of 3: invite the bots to your server</h2>")
            .Append("<p>Choose each bot's Invite button, pick your Discord server, and choose <b>Authorize</b>. Start with the bot that runs the commands. This page checks again every few seconds.</p>");
        AppendProblem(body, view.Problem);
        body.Append("<table><thead><tr><th scope=\"col\">Bot</th><th scope=\"col\">In a server?</th><th scope=\"col\"></th></tr></thead><tbody>");
        foreach (var bot in view.Bots)
        {
            var state = bot.Servers switch
            {
                null => "<span class=\"wait\">Checking with Discord…</span>",
                { Count: 0 } => "<span class=\"no\">&#x2717; Not yet</span>",
                var servers => "<span class=\"yes\">&#x2713; In " + E(string.Join(", ", servers.Select(s => s.Name))) + "</span>",
            };
            var link = InviteLink(bot.ApplicationId, bot.Primary, bot.Primary ? null : home);
            body.Append("<tr><td>").Append(E($"{RelayNames.ForSlot(bot.Slot)}: {bot.Name}")).Append(bot.Primary ? " <small>(runs the commands)</small>" : "").Append("</td>")
                .Append("<td>").Append(state).Append("</td>")
                .Append("<td><a class=\"button\" href=\"").Append(E(link.AbsoluteUri)).Append("\" target=\"_blank\" rel=\"noopener noreferrer\">").Append(E($"Invite {RelayNames.ForSlot(bot.Slot)}")).Append("</a></td></tr>");
        }
        body.Append("</tbody></table>");
        var everyone = view.Bots.All(b => b.Servers is { Count: > 0 });
        if (view.Shared.Count > 0)
        {
            body.Append("<form method=\"post\" action=\"/setup/finish\">");
            if (view.Shared.Count > 1)
            {
                body.Append("<fieldset><legend>The bots share several servers. Which one is the guild's?</legend>");
                foreach (var server in view.Shared)
                {
                    body.Append(CultureInfo.InvariantCulture, $"<label class=\"choice\"><input type=\"radio\" name=\"server\" value=\"{server.Id}\" required> {E(server.Name)}</label>");
                }
                body.Append("</fieldset>");
            }
            else
            {
                body.Append(CultureInfo.InvariantCulture, $"<input type=\"hidden\" name=\"server\" value=\"{view.Shared[0].Id}\">")
                    .Append("<p>Every bot is in <b>").Append(E(view.Shared[0].Name)).Append("</b>.</p>");
            }
            body.Append("<button type=\"submit\">Finish setup</button></form>");
        }
        else if (everyone)
        {
            body.Append("<p class=\"error\" role=\"alert\">The bots are in different servers. Invite them all to the same one.</p>");
        }
        body.Append("<form method=\"post\" action=\"/setup/back\"><button type=\"submit\" class=\"secondary\">Paste different tokens</button></form>");
        return Page("Invite the bots", body.ToString(), refresh: view.Shared.Count == 0);
    }

    private static string Done() => Page("Setup is done", """
        <h2>Step 3 of 3: done</h2>
        <p>The tokens are saved on this computer and the bot is starting. In Discord, in your server:</p>
        <ol>
        <li>Type <code>/gvg setup</code> and pick the Attack voice channels and the Defense voice channels. A relay bot joins each channel: A1 Broadcaster the first Attack channel, D1 Broadcaster the first Defense channel, and so on.</li>
        <li>Optional: in the same command, pick a text channel for <b>channel</b>. The bot posts problems there, such as a relay bot going offline.</li>
        <li>Players type <code>/connect</code> and paste the code into the Kzg app.</li>
        </ol>
        <p>Signing in with Discord in the app is optional and needs more setup; <code>/connect</code> codes always work.</p>
        <p>You can close this page.</p>
        """);

    private static void AppendProblem(StringBuilder body, string? problem)
    {
        if (problem is not null)
        {
            body.Append("<p class=\"error\" role=\"alert\">").Append(E(problem)).Append("</p>");
        }
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);

    private static string Page(string title, string body, bool refresh = false) =>
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">"
        + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
        + (refresh ? "<meta http-equiv=\"refresh\" content=\"5\">" : "")
        + $"<title>{E(title)} - Kzg bot setup</title><style>{Style}</style></head>"
        + $"<body><main><h1>Kzg bot setup</h1>{body}</main></body></html>\n";

    private const string Style = """
        :root{color-scheme:light dark;--fg:#1c1c1e;--bg:#f5f5f7;--card:#fff;--line:#d1d1d6;--accent:#4752c4;--bad:#b3261e;--good:#1b7f3b}
        @media (prefers-color-scheme:dark){:root{--fg:#f2f2f7;--bg:#1c1c1e;--card:#2c2c2e;--line:#48484a;--accent:#8c96ff;--bad:#ff8a80;--good:#7ee2a0}}
        body{margin:0;font:16px/1.5 system-ui,-apple-system,"Segoe UI",sans-serif;color:var(--fg);background:var(--bg)}
        main{max-width:44rem;margin:2rem auto;padding:1.5rem 2rem;background:var(--card);border:1px solid var(--line);border-radius:12px}
        h1{font-size:1.1rem;margin:0 0 1rem;opacity:.8}h2{font-size:1.4rem;margin:.5rem 0 1rem}
        label{display:block;margin-top:1rem;font-weight:600}
        input[type=password],input[type=text]{box-sizing:border-box;width:100%;padding:.6rem;font:inherit;border:1px solid var(--line);border-radius:8px;background:var(--bg);color:var(--fg)}
        button,a.button{display:inline-block;margin-top:1.25rem;padding:.6rem 1.2rem;font:inherit;font-weight:600;color:#fff;background:var(--accent);border:0;border-radius:8px;text-decoration:none;cursor:pointer}
        a.button{margin:0;padding:.35rem .9rem}button.secondary{background:transparent;color:var(--accent);border:1px solid var(--accent)}
        button:focus-visible,a:focus-visible,input:focus-visible{outline:3px solid var(--accent);outline-offset:2px}
        table{width:100%;border-collapse:collapse;margin-top:1rem}th,td{text-align:left;padding:.5rem;border-bottom:1px solid var(--line)}
        .error{color:var(--bad);font-weight:600}.yes{color:var(--good);font-weight:600}.no{color:var(--bad)}.hint{font-size:.9rem;opacity:.8}
        label.choice{font-weight:400}fieldset{margin-top:1rem;border:1px solid var(--line);border-radius:8px}
        code{font-size:.95em}details{margin:1rem 0}
        """;
}
