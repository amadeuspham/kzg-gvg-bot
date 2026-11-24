namespace Kzg.App.Link;

/// <summary>
/// One WebSocket to the bot. The events arrive on the thread the controller runs on, so the controller
/// needs no locks; the real transport hops there itself. Sends may be issued back to back: they go out
/// in order, one at a time.
/// </summary>
public interface ILinkTransport : IAsyncDisposable
{
    /// <summary>One whole message as UTF-8, valid only for the duration of the call.</summary>
    event Action<ReadOnlyMemory<byte>>? Received;

    /// <summary>
    /// The connection ended after it was open: closed by the bot, broken, or sent something too large. The
    /// argument is the WebSocket close code the bot sent, or null when there was none.
    /// </summary>
    event Action<int?>? Closed;

    /// <summary>Throws when the server can't be reached or refuses the upgrade.</summary>
    Task ConnectAsync(Uri uri, CancellationToken cancellation);

    Task SendAsync(ReadOnlyMemory<byte> message);
}

public interface ILinkTransportFactory
{
    ILinkTransport Create();
}
