using System.Net;
using Kzg.Bot.Logging;
using Kzg.Core.Config;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Kzg.Bot.Setup;

/// <summary>
/// The setup page on its own Kestrel, served only until setup is done; then it stops and the bot proper
/// takes the port. Every request but <c>/healthz</c> must pass <see cref="Admits"/>: a browser on this
/// computer, or one coming through the HTTPS proxy to the public address, holding the session the
/// one-time key printed in the console opened.
/// </summary>
public sealed class SetupServer : IAsyncDisposable
{
    public const string CookieName = "kzg_setup";

    private const int MaxBodyBytes = 16 * 1024;

    private readonly WebApplication _web;
    private readonly SetupWizard _wizard;
    private readonly string? _publicHost;

    private SetupServer(WebApplication web, SetupWizard wizard, string? publicHost)
    {
        (_web, _wizard, _publicHost) = (web, wizard, publicHost);
        Map(web);
    }

    public SetupWizard Wizard => _wizard;

    public Uri Address
    {
        get
        {
            var addresses = _web.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
            return new Uri(addresses?.FirstOrDefault() ?? throw new InvalidOperationException("the setup page isn't listening yet"));
        }
    }

    /// <param name="publicHost">The players' address, as the proxy passes it on; requests through the proxy must name it or this computer.</param>
    public static SetupServer Create(SetupWizard wizard, IPEndPoint listen, string? publicHost, TextWriter log, Redactor redactor)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.AddRedactingLogger(log, redactor, LogLevel.Information, quietFramework: true);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = MaxBodyBytes;
            kestrel.Listen(listen);
        });
        return new SetupServer(builder.Build(), wizard, publicHost);
    }

    public Task StartAsync(CancellationToken cancellationToken) => _web.StartAsync(cancellationToken);

    public Task StopAsync() => _web.StopAsync();

    public async ValueTask DisposeAsync() => await _web.DisposeAsync().ConfigureAwait(false);

    /// <summary>
    /// Whether a request may see the page at all. Straight to this port, the Host must be this computer, which
    /// also stops a web page from reaching it by a name of its own (DNS rebinding). Through a proxy, the browser
    /// must have used HTTPS, so a token never crosses a network in plain text, and the Host is the public address
    /// or this computer. The session cookie is checked after this.
    /// </summary>
    public static bool Admits(HttpRequest request, string? publicHost)
    {
        var host = request.Host.Host;
        var local = host is "localhost" or "127.0.0.1" or "[::1]" or "::1";
        var proxied = request.Headers.ContainsKey("X-Forwarded-For") || request.Headers.ContainsKey("X-Forwarded-Proto")
            || request.Headers.ContainsKey("Forwarded") || request.Headers.ContainsKey("X-Real-IP");
        if (!proxied)
        {
            return local;
        }
        var https = string.Equals(First(request.Headers["X-Forwarded-Proto"]), "https", StringComparison.OrdinalIgnoreCase)
            || request.Headers["Forwarded"].ToString().Contains("proto=https", StringComparison.OrdinalIgnoreCase);
        return https && (local || publicHost is not null && string.Equals(host, HostOf(publicHost), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A form posted from another site carries an Origin or a fetch-site that gives it away.</summary>
    private static bool SameOrigin(HttpRequest request)
    {
        if (string.Equals(request.Headers["Sec-Fetch-Site"].ToString(), "cross-site", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var origin = request.Headers.Origin.ToString();
        return origin.Length == 0 || Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static string HostOf(string publicHost)
    {
        // "name:port" or "[v6]:port": the Host header's host part is the same text without the port.
        var colon = publicHost.LastIndexOf(':');
        return colon > 0 && !publicHost.EndsWith(']') ? publicHost[..colon] : publicHost;
    }

    private static string? First(StringValues values) => values.ToString().Split(',')[0].Trim() is { Length: > 0 } first ? first : null;

    private void Map(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            foreach (var (name, value) in Headers)
            {
                context.Response.Headers[name] = value;
            }
            await next(context).ConfigureAwait(false);
        });
        app.MapGet("/healthz", () => Results.Text("ok\n"));
        app.MapGet("/setup", async (HttpContext context) =>
        {
            if (!Admits(context.Request, _publicHost))
            {
                return Forbidden();
            }
            if (context.Request.Query.ContainsKey("key"))
            {
                switch (_wizard.UseKey(context.Request.Query["key"], out var session))
                {
                    case KeyUse.Accepted:
                        context.Response.Cookies.Append(CookieName, session!, new CookieOptions
                        {
                            HttpOnly = true,
                            SameSite = SameSiteMode.Strict,
                            Path = "/setup",
                            Secure = context.Request.Headers.ContainsKey("X-Forwarded-Proto") || context.Request.Headers.ContainsKey("Forwarded"),
                            IsEssential = true,
                        });
                        // The key leaves the address bar and the history.
                        return Results.Redirect("/setup");
                    case KeyUse.Spent:
                        return Html(410, SetupPages.Message("This link was used already", "Each setup link opens the page once. A new link is in the bot's window or log now."));
                    default:
                        return Forbidden();
                }
            }
            if (!_wizard.IsSession(context.Request.Cookies[CookieName]))
            {
                return Forbidden();
            }
            await _wizard.LookAsync(context.RequestAborted).ConfigureAwait(false);
            return Html(200, SetupPages.Render(_wizard.View()));
        });
        app.MapPost("/setup/tokens", (Delegate)((HttpContext context) => Post(context, async form =>
        {
            var tokens = Enumerable.Range(1, SetupWizard.MaxBots).Select(i => (string?)form[$"token{i}"].ToString()).ToList();
            await _wizard.SubmitAsync(tokens, form["publicHost"].ToString(), context.RequestAborted).ConfigureAwait(false);
            if (_wizard.View().Step == SetupStep.Invite)
            {
                await _wizard.LookAsync(context.RequestAborted).ConfigureAwait(false);
            }
            return SeeOther(context);
        })));
        app.MapPost("/setup/back", (Delegate)((HttpContext context) => Post(context, _ =>
        {
            _wizard.Back();
            return Task.FromResult(SeeOther(context));
        })));
        app.MapPost("/setup/finish", (Delegate)((HttpContext context) => Post(context, async form =>
        {
            ulong? server = Protocol.Snowflake.TryParse(form["server"].ToString(), out var id) ? id : null;
            if (!await _wizard.FinishAsync(server, context.RequestAborted).ConfigureAwait(false))
            {
                return SeeOther(context);
            }
            // The bot goes on once this page has gone out, and it takes the port the page is on.
            var written = _wizard.Written!;
            context.Response.OnCompleted(() =>
            {
                _wizard.Release(written);
                return Task.CompletedTask;
            });
            return Html(200, SetupPages.Render(_wizard.View()));
        })));
    }

    private async Task<IResult> Post(HttpContext context, Func<IFormCollection, Task<IResult>> handle)
    {
        var request = context.Request;
        if (!Admits(request, _publicHost) || !SameOrigin(request) || !_wizard.IsSession(request.Cookies[CookieName]) || !request.HasFormContentType)
        {
            return Forbidden();
        }
        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(new FormOptions { ValueCountLimit = 16, KeyLengthLimit = 32, ValueLengthLimit = 1024, MultipartBodyLengthLimit = MaxBodyBytes }, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or BadHttpRequestException or IOException)
        {
            return Results.BadRequest();
        }
        return await handle(form).ConfigureAwait(false);
    }

    /// <summary>After a form, back to the page by GET, so a reload doesn't post the form again.</summary>
    private static IResult SeeOther(HttpContext context)
    {
        context.Response.Headers.Location = "/setup";
        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }

    private static IResult Forbidden() => Html(403, SetupPages.Message(
        "Open the setup link",
        "Open the setup link the bot printed when it started, in a browser on the same computer. It looks like http://localhost:7878/setup?key=..."));

    private static IResult Html(int status, string html) => Results.Content(html, "text/html; charset=utf-8", statusCode: status);

    /// <summary>On every reply: nothing cached, nothing passed to other sites, no framing, no script.</summary>
    private static readonly (string Name, string Value)[] Headers =
    [
        ("Cache-Control", "no-store"),
        // Not no-referrer: a browser then sends "Origin: null" on the page's own forms, which SameOrigin must refuse.
        ("Referrer-Policy", "same-origin"),
        ("X-Content-Type-Options", "nosniff"),
        ("X-Frame-Options", "DENY"),
        ("Content-Security-Policy", "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'"),
    ];
}
