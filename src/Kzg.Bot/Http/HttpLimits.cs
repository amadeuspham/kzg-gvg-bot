namespace Kzg.Bot.Http;

/// <summary>A token bucket: <see cref="Burst"/> at once, then one more every <see cref="Every"/>.</summary>
public sealed record Rate(int Burst, TimeSpan Every);

/// <summary>
/// How much of the public server one client address may use, and all of them together. The host's
/// address is in every connect code, so anyone may learn it; these keep a stranger from holding every
/// socket or flooding the sign-in routes. A client is an IPv4 address or an IPv6 /64, which one home
/// connection is usually given whole. Over a limit the request gets 429 before anything else runs.
/// </summary>
public sealed record HttpLimits
{
    /// <summary>
    /// New <c>/ws</c> connections per client. An app connects once and reconnects at most every 10 s
    /// after its first failures, so 30 covers a household or a LAN party restarting together.
    /// </summary>
    public Rate WsConnects { get; init; } = new(30, TimeSpan.FromSeconds(2));

    /// <summary>
    /// Open <c>/ws</c> connections per client. One player keeps one, but a connection waiting on a
    /// Discord sign-in may stay up to 10 minutes before it is signed in, so this bounds the waiting ones.
    /// </summary>
    public int WsOpenPerClient { get; init; } = 32;

    /// <summary>Open <c>/ws</c> connections in all, well above any guild's players.</summary>
    public int WsOpenTotal { get; init; } = 1024;

    /// <summary><c>/auth/start</c> per client: a player opens it once per sign-in.</summary>
    public Rate AuthStarts { get; init; } = new(10, TimeSpan.FromSeconds(6));

    /// <summary><c>/auth/callback</c> per client: Discord sends the browser back once per sign-in.</summary>
    public Rate AuthCallbacks { get; init; } = new(10, TimeSpan.FromSeconds(6));

    /// <summary>
    /// TCP connections Kestrel keeps at once, before any request is read; WebSockets count apart, under
    /// <see cref="WsOpenTotal"/>. Behind a proxy these are the proxy's, so it is a backstop only.
    /// </summary>
    public int Connections { get; init; } = 2048;
}
