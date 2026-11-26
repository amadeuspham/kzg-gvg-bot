using Kzg.Bot.Control;
using Kzg.Core.Pairing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Kzg.Bot.Http;

/// <summary>Maps the bot's four routes onto the control server and the auth endpoints; they hold the logic.</summary>
internal static class Endpoints
{
    public static void Map(WebApplication app, ControlServer control, AuthEndpoints auth, RateLimits limits)
    {
        limits.Use(app);
        app.UseWebSockets();
        app.MapGet("/ws", async (HttpContext context, IHostApplicationLifetime lifetime) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await control.RunAsync(socket, lifetime.ApplicationStopping).ConfigureAwait(false);
        }).WithMetadata(new LimitedRouteMetadata(LimitedRoute.Ws));
        app.MapGet("/auth/start", (HttpContext context) => Write(context, auth.Start(context.Request.Query["login"])))
            .WithMetadata(new LimitedRouteMetadata(LimitedRoute.AuthStart));
        app.MapGet("/auth/callback", async (HttpContext context) =>
        {
            var query = context.Request.Query;
            await Write(context, await auth.CallbackAsync(query["code"], query["state"], query["error"], context.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
        }).WithMetadata(new LimitedRouteMetadata(LimitedRoute.AuthCallback));
        app.MapGet("/healthz", (HttpContext context) => Write(context, AuthEndpoints.Healthz()));
    }

    private static Task Write(HttpContext context, HttpReply reply)
    {
        var response = context.Response;
        response.StatusCode = reply.Status;
        response.ContentType = reply.ContentType;
        foreach (var (name, value) in HttpReply.Headers)
        {
            response.Headers[name] = value;
        }
        if (reply.Location is { } location)
        {
            response.Headers.Location = location.AbsoluteUri;
        }
        return response.WriteAsync(reply.Body);
    }
}
