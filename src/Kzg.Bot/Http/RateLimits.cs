using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Kzg.Bot.Control;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using IPNetwork = System.Net.IPNetwork;

namespace Kzg.Bot.Http;

/// <summary>Which limits a route is under; routes without it, such as <c>/healthz</c>, have none.</summary>
internal enum LimitedRoute
{
    Ws,
    AuthStart,
    AuthCallback,
}

/// <summary>
/// The per-client and total limits of <see cref="HttpLimits"/> as ASP.NET's rate limiter, and the
/// forwarded-header handling that decides who the client is. The route is told by its endpoint, not
/// its path, so <c>/WS</c> or <c>/ws/</c> is limited exactly as <c>/ws</c>.
/// </summary>
internal sealed class RateLimits(HttpLimits limits, IReadOnlyList<IPNetwork> trustedProxies, ILogger log, TimeProvider time)
{
    private static readonly TimeSpan WarnEvery = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private DateTimeOffset _warnedAt = DateTimeOffset.MinValue;
    private int _refused;
    private bool _warnedUntrusted;

    private void Configure(RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<HttpContext, string>(context => RouteOf(context) switch
            {
                LimitedRoute.Ws => Bucket("ws " + ClientOf(context), limits.WsConnects),
                LimitedRoute.AuthStart => Bucket("start " + ClientOf(context), limits.AuthStarts),
                LimitedRoute.AuthCallback => Bucket("callback " + ClientOf(context), limits.AuthCallbacks),
                _ => RateLimitPartition.GetNoLimiter(""),
            }),
            PartitionedRateLimiter.Create<HttpContext, string>(context => RouteOf(context) == LimitedRoute.Ws
                ? Open(ClientOf(context), limits.WsOpenPerClient)
                : RateLimitPartition.GetNoLimiter("")),
            PartitionedRateLimiter.Create<HttpContext, string>(context => RouteOf(context) == LimitedRoute.Ws
                ? Open("all", limits.WsOpenTotal)
                : RateLimitPartition.GetNoLimiter("")));
        options.OnRejected = (rejected, cancellationToken) =>
        {
            var response = rejected.HttpContext.Response;
            foreach (var (name, value) in HttpReply.Headers)
            {
                response.Headers[name] = value;
            }
            if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            {
                response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            response.ContentType = "text/plain; charset=utf-8";
            NoteRefused(RouteOf(rejected.HttpContext));
            return new ValueTask(response.WriteAsync("Too many requests. Try again in a minute.\n", cancellationToken));
        };
    }

    /// <summary>
    /// Runs first: believes the last <c>X-Forwarded-For</c> hop only from a trusted proxy, and says once
    /// when a proxy that isn't trusted sends it, since every app behind that proxy then shares its limits.
    /// </summary>
    public void Use(WebApplication app)
    {
        app.Use((context, next) =>
        {
            if (!_warnedUntrusted && context.Request.Headers.ContainsKey("X-Forwarded-For") && !IsTrusted(context.Connection.RemoteIpAddress))
            {
                _warnedUntrusted = true;
                log.LogWarning("a request came through a proxy that isn't in trustedProxies, so its X-Forwarded-For is ignored and everyone behind it shares one rate limit; add the proxy's address to trustedProxies in config.json");
            }
            return next(context);
        });
        if (trustedProxies.Count > 0)
        {
            var forwarding = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor, ForwardLimit = 1 };
            // The defaults trust loopback; only what config.json lists counts.
            forwarding.KnownIPNetworks.Clear();
            forwarding.KnownProxies.Clear();
            foreach (var network in trustedProxies)
            {
                forwarding.KnownIPNetworks.Add(network);
            }
            app.UseForwardedHeaders(forwarding);
        }
        app.UseRouting();
        var options = new RateLimiterOptions();
        Configure(options);
        app.UseRateLimiter(options);
    }

    /// <summary>An IPv4 address, or the /64 an IPv6 address is in, since one home is given a whole /64.</summary>
    internal static string ClientOf(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    private static string ClientOf(HttpContext context) => ClientOf(context.Connection.RemoteIpAddress);

    private static LimitedRoute? RouteOf(HttpContext context) => context.GetEndpoint()?.Metadata.GetMetadata<LimitedRouteMetadata>()?.Route;

    private static RateLimitPartition<string> Bucket(string key, Rate rate) =>
        RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = rate.Burst,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = rate.Every,
            QueueLimit = 0,
            AutoReplenishment = true,
        });

    // The lease is held while the endpoint runs, which for /ws is the socket's whole life.
    private static RateLimitPartition<string> Open(string key, int limit) =>
        RateLimitPartition.GetConcurrencyLimiter(key, _ => new ConcurrencyLimiterOptions { PermitLimit = limit, QueueLimit = 0 });

    private bool IsTrusted(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        foreach (var network in trustedProxies)
        {
            if (network.Contains(address))
            {
                return true;
            }
        }
        return false;
    }

    // One line a minute at most, so a flood can't fill the log; never the address, which is a player's.
    private void NoteRefused(LimitedRoute? route)
    {
        int refused;
        lock (_gate)
        {
            _refused++;
            var now = time.GetUtcNow();
            if (now - _warnedAt < WarnEvery)
            {
                return;
            }
            (_warnedAt, refused, _refused) = (now, _refused, 0);
        }
        log.LogWarning("refused {Count} request(s) over the rate limits, the latest to {Route}", refused, route?.ToString() ?? "unknown");
    }
}

/// <summary>Endpoint metadata naming the limits a route is under.</summary>
internal sealed record LimitedRouteMetadata(LimitedRoute Route);
