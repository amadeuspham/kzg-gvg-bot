using System.Net.WebSockets;
using Kzg.Protocol;

namespace Kzg.App.Link;

public sealed class WebSocketLinkTransportFactory(Action<Action> post) : ILinkTransportFactory
{
    public ILinkTransport Create() => new WebSocketLinkTransport(post);
}

/// <summary>
/// The real transport. Reads with <see cref="MessageAssembler"/>, so a peer can never make it hold more
/// than the 4 KB message cap, and closes on a binary or oversized message.
/// </summary>
public sealed class WebSocketLinkTransport(Action<Action> post) : ILinkTransport
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendTurn = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private int _disposed;

    public event Action<ReadOnlyMemory<byte>>? Received;

    public event Action<int?>? Closed;

    public async Task ConnectAsync(Uri uri, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _life.Token);
        linked.CancelAfter(ConnectTimeout);
        await _socket.ConnectAsync(uri, linked.Token);
        _ = ReceiveLoopAsync();
    }

    public async Task SendAsync(ReadOnlyMemory<byte> message)
    {
        await _sendTurn.WaitAsync(_life.Token);
        try
        {
            await _socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, _life.Token);
        }
        finally
        {
            _sendTurn.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", patience.Token);
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Going away anyway.
            }
        }
        await _life.CancelAsync();
        _socket.Dispose();
        _sendTurn.Dispose();
    }

    private async Task ReceiveLoopAsync()
    {
        var assembler = new MessageAssembler();
        int? closeCode = null;
        try
        {
            while (!_life.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(assembler.Remaining, _life.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    closeCode = (int?)_socket.CloseStatus;
                    break;
                }
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    break;
                }
                var state = assembler.Advance(result.Count, result.EndOfMessage);
                if (state == AssemblyState.TooLarge)
                {
                    break;
                }
                if (state == AssemblyState.Complete)
                {
                    var message = assembler.Message.ToArray();
                    assembler.Reset();
                    post(() => Received?.Invoke(message));
                }
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // A broken connection ends the loop like a closed one.
        }
        if (Volatile.Read(ref _disposed) == 0)
        {
            post(() => Closed?.Invoke(closeCode));
        }
    }
}
