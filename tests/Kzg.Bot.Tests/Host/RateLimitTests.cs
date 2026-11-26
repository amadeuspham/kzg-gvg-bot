using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Kzg.Bot.Http;
using Kzg.Core.Config;
using Kzg.Protocol;

namespace Kzg.Bot.Tests.Hosting;

// The public routes' limits on the real host. Buckets refill once an hour here, so nothing waits on the clock.
public sealed class RateLimitTests : IAsyncDisposable
{
    private static readonly TimeSpan Never = TimeSpan.FromHours(1);
    private static readonly HttpLimits Roomy = new()
    {
        WsConnects = new Rate(100, Never),
        AuthStarts = new Rate(100, Never),
        AuthCallbacks = new Rate(100, Never),
    };

    private readonly List<IAsyncDisposable> _cleanup = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var item in Enumerable.Reverse(_cleanup))
        {
            await item.DisposeAsync();
        }
    }

    private async Task<HostRig> RigAsync(HttpLimits limits, Func<BotConfig, BotConfig>? config = null)
    {
        var rig = new HostRig(config, limits: limits);
        _cleanup.Add(rig);
        return await rig.StartAsync();
    }

    private static async Task<HttpStatusCode> GetAsync(HostRig rig, string path, string? forwardedFor = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, rig.Url(path));
        if (forwardedFor is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        }
        using var response = await rig.Http.SendAsync(request);
        return response.StatusCode;
    }

    /// <summary>Opens <c>/ws</c> and, if the bot took it, says hello with a login nobody completes, as a stranger would to hold the socket.</summary>
    private async Task<(ClientWebSocket Socket, HttpStatusCode Status)> OpenAsync(HostRig rig, string? forwardedFor = null)
    {
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        if (forwardedFor is not null)
        {
            socket.Options.SetRequestHeader("X-Forwarded-For", forwardedFor);
        }
        try
        {
            await socket.ConnectAsync(new UriBuilder(rig.Bot.Address) { Scheme = "ws", Path = "/ws" }.Uri, CancellationToken.None);
        }
        catch (WebSocketException)
        {
            var status = socket.HttpStatusCode;
            socket.Dispose();
            return (socket, status);
        }
        var app = new AppClient(socket);
        _cleanup.Add(app);
        await app.SendAsync(new HelloMessage("1.0.0", Guid.NewGuid(), LoginId: WireSecret.Generate()));
        return (socket, HttpStatusCode.SwitchingProtocols);
    }

    [Fact]
    public async Task New_ws_connections_past_a_clients_burst_get_429_whatever_the_paths_spelling_and_healthz_is_never_limited()
    {
        var rig = await RigAsync(Roomy with { WsConnects = new Rate(3, Never) });

        var first = await OpenAsync(rig);
        var plain = await GetAsync(rig, "/ws");
        var upper = await GetAsync(rig, "/WS");
        var refused = await OpenAsync(rig);
        using var request = new HttpRequestMessage(HttpMethod.Get, rig.Url("/ws/"));
        using var reply = await rig.Http.SendAsync(request);
        var health = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => GetAsync(rig, "/healthz")));

        Assert.Equal(HttpStatusCode.SwitchingProtocols, first.Status);
        Assert.Equal(HttpStatusCode.BadRequest, plain);
        Assert.Equal(HttpStatusCode.BadRequest, upper);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, reply.StatusCode);
        Assert.Equal("no-store", reply.Headers.CacheControl?.ToString());
        Assert.True(int.Parse(reply.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture) >= 1);
        Assert.All(health, status => Assert.Equal(HttpStatusCode.OK, status));
        Assert.Contains("refused 1 request(s) over the rate limits, the latest to Ws", rig.LogText);
        Assert.DoesNotContain("127.0.0.1", rig.LogText.Split('\n').Single(line => line.Contains("refused", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_client_holds_only_so_many_open_sockets_and_gets_one_back_when_one_closes()
    {
        var rig = await RigAsync(Roomy with { WsOpenPerClient = 2 });

        var one = await OpenAsync(rig);
        var two = await OpenAsync(rig);
        var three = await OpenAsync(rig);
        one.Socket.Abort();
        var again = HttpStatusCode.TooManyRequests;
        for (var attempt = 0; attempt < 50 && again == HttpStatusCode.TooManyRequests; attempt++)
        {
            // The bot frees the slot once it notices the socket is gone.
            await Task.Delay(100);
            again = (await OpenAsync(rig)).Status;
        }

        Assert.Equal(HttpStatusCode.SwitchingProtocols, one.Status);
        Assert.Equal(HttpStatusCode.SwitchingProtocols, two.Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, three.Status);
        Assert.Equal(HttpStatusCode.SwitchingProtocols, again);
    }

    [Fact]
    public async Task Open_sockets_are_capped_in_all_whichever_clients_hold_them()
    {
        var rig = await RigAsync(Roomy with { WsOpenTotal = 2 });

        var a = await OpenAsync(rig, "203.0.113.1");
        var b = await OpenAsync(rig, "203.0.113.2");
        var c = await OpenAsync(rig, "203.0.113.3");

        Assert.Equal(HttpStatusCode.SwitchingProtocols, a.Status);
        Assert.Equal(HttpStatusCode.SwitchingProtocols, b.Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, c.Status);
    }

    [Fact]
    public async Task Behind_a_trusted_proxy_each_player_has_their_own_limits_keyed_by_the_last_hop()
    {
        // Loopback is among the default trusted proxies.
        var rig = await RigAsync(Roomy with { WsConnects = new Rate(1, Never) });

        var alice = await GetAsync(rig, "/ws", "203.0.113.1");
        var aliceAgain = await GetAsync(rig, "/ws", "203.0.113.1");
        // A forged first hop is ignored: the proxy appended the real address last.
        var forged = await GetAsync(rig, "/ws", "198.51.100.7, 203.0.113.1");
        var bob = await GetAsync(rig, "/ws", "203.0.113.2");

        Assert.Equal(HttpStatusCode.BadRequest, alice);
        Assert.Equal(HttpStatusCode.TooManyRequests, aliceAgain);
        Assert.Equal(HttpStatusCode.TooManyRequests, forged);
        Assert.Equal(HttpStatusCode.BadRequest, bob);
        Assert.DoesNotContain("isn't in trustedProxies", rig.LogText);
    }

    [Fact]
    public async Task An_ipv6_client_is_its_whole_64()
    {
        var rig = await RigAsync(Roomy with { WsConnects = new Rate(1, Never) });

        var first = await GetAsync(rig, "/ws", "2001:db8:1:2::1");
        var sameHome = await GetAsync(rig, "/ws", "2001:db8:1:2:ffff:ffff:ffff:ffff");
        var otherHome = await GetAsync(rig, "/ws", "2001:db8:1:3::1");

        Assert.Equal(HttpStatusCode.BadRequest, first);
        Assert.Equal(HttpStatusCode.TooManyRequests, sameHome);
        Assert.Equal(HttpStatusCode.BadRequest, otherHome);
    }

    [Fact]
    public async Task With_no_trusted_proxy_the_header_is_ignored_and_the_admin_is_told_once()
    {
        var rig = await RigAsync(Roomy with { WsConnects = new Rate(1, Never) }, c => c with { TrustedProxies = [] });

        var alice = await GetAsync(rig, "/ws", "203.0.113.1");
        var bob = await GetAsync(rig, "/ws", "203.0.113.2");
        var carol = await GetAsync(rig, "/ws", "203.0.113.3");

        Assert.Equal(HttpStatusCode.BadRequest, alice);
        Assert.Equal(HttpStatusCode.TooManyRequests, bob);
        Assert.Equal(HttpStatusCode.TooManyRequests, carol);
        Assert.Single(rig.LogText.Split('\n'), line => line.Contains("isn't in trustedProxies", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_proxy_that_is_not_listed_is_not_believed_even_when_others_are()
    {
        var rig = await RigAsync(Roomy with { WsConnects = new Rate(1, Never) }, c => c with { TrustedProxies = ["10.9.8.7", "fd00::/8"] });

        var alice = await GetAsync(rig, "/ws", "203.0.113.1");
        var bob = await GetAsync(rig, "/ws", "203.0.113.2");

        Assert.Equal(HttpStatusCode.BadRequest, alice);
        Assert.Equal(HttpStatusCode.TooManyRequests, bob);
    }

    [Fact]
    public async Task Sign_in_starts_and_callbacks_have_buckets_of_their_own()
    {
        var rig = await RigAsync(Roomy with { AuthStarts = new Rate(2, Never), AuthCallbacks = new Rate(1, Never) });
        var login = WireSecret.Generate();

        var starts = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            starts.Add(await GetAsync(rig, $"/auth/start?login={login}"));
        }
        var callback = await GetAsync(rig, $"/auth/callback?code=x&state={login}");
        var callbackAgain = await GetAsync(rig, $"/auth/callback?code=x&state={login}");
        var ws = await GetAsync(rig, "/ws");

        Assert.Equal([HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests], starts);
        Assert.Equal(HttpStatusCode.NotFound, callback);
        Assert.Equal(HttpStatusCode.TooManyRequests, callbackAgain);
        Assert.Equal(HttpStatusCode.BadRequest, ws);
    }

    [Fact]
    public async Task Kestrel_drops_tcp_connections_past_its_cap_before_reading_a_request()
    {
        var rig = await RigAsync(Roomy with { Connections = 1 });
        var port = rig.Bot.Address.Port;

        using var held = new TcpClient();
        await held.ConnectAsync(IPAddress.Loopback, port);
        await Task.Delay(200);
        using var extra = new TcpClient();
        await extra.ConnectAsync(IPAddress.Loopback, port);
        var stream = extra.GetStream();
        await stream.WriteAsync("GET /healthz HTTP/1.1\r\nHost: localhost\r\n\r\n"u8.ToArray());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[256];
        int read;
        try
        {
            read = await stream.ReadAsync(buffer, timeout.Token);
        }
        catch (IOException)
        {
            read = 0;
        }

        Assert.Equal(0, read);
    }
}
