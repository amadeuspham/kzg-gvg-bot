using System.Net.WebSockets;
using System.Threading.Channels;
using Kzg.Core.Abstractions;
using Kzg.Core.Pairing;
using Kzg.Protocol;

namespace Kzg.Bot.Control;

/// <summary>
/// One app's WebSocket. Three loops share it: the reader, which parses and dispatches; the writer,
/// the only sender, which the outbox feeds in order; and the watchdog, which closes a connection
/// that missed its hello or went silent. A close is always sent by the writer, so it follows every
/// message queued before it. Locks are taken server first, then connection, never the other way:
/// nothing calls into the server while holding this connection's lock.
/// </summary>
internal sealed class Connection(ControlServer server, WebSocket socket) : IDisposable
{
    private readonly Channel<Outgoing> _outbox = Channel.CreateUnbounded<Outgoing>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _gate = new();
    private readonly long _origin = server.Timers.GetTimestamp();

    private Task _reading = Task.CompletedTask;
    private bool _closing;
    private Phase _phase;
    private TimeSpan? _deadline;

    // Register and Unregister each happen once, in that order, whichever of sign-in and the end comes last.
    private bool _registered;
    private bool _ended;

    // Set once the hello is accepted.
    private UserId? _user;
    private string? _tokenHash;
    private string? _loginHash;

    private byte[]? _pendingStatus;
    private bool _statusScheduled;
    private TimeSpan _lastStatus = TimeSpan.MinValue;

    private long _pressSeq = -1;
    private long _openMicSeq = -1;
    private long _heartbeatSeq = -1;

    private TimeSpan Now => server.Timers.GetElapsedTime(_origin);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var stop = cancellationToken.Register(() => Close(CloseCodes.GoingAway, "server shutting down"));
        lock (_gate)
        {
            _deadline = Now + server.Options.HelloDeadline;
        }
        _reading = ReadAsync();
        var writing = WriteAsync();
        var watching = WatchAsync();
        try
        {
            await _reading.ConfigureAwait(false);
        }
        finally
        {
            // The writer finishes what it has; a peer that went without closing leaves nothing to send to.
            _outbox.Writer.TryComplete();
            await Task.WhenAny(writing, Task.Delay(server.Options.CloseGrace, server.Timers)).ConfigureAwait(false);
            await _lifetime.CancelAsync().ConfigureAwait(false);
            if (socket.State is not WebSocketState.Closed)
            {
                socket.Abort();
            }
            await Task.WhenAll(writing, watching).ConfigureAwait(false);
            lock (_gate)
            {
                if (_loginHash is { } login && _user is null)
                {
                    server.Logins.Abandon(login);
                }
            }
            bool registered;
            lock (_gate)
            {
                (_ended, registered) = (true, _registered);
            }
            if (registered)
            {
                server.Unregister(this, _user!.Value);
            }
        }
    }

    public void Dispose() => _lifetime.Dispose();

    /// <summary>Queues a message the writer sends as it is.</summary>
    public void Queue(byte[] message)
    {
        lock (_gate)
        {
            if (!_closing)
            {
                _outbox.Writer.TryWrite(new Outgoing(message, null, null));
            }
        }
    }

    /// <summary>Keeps the newest status and sends it as soon as the rate allows.</summary>
    public void QueueStatus(byte[] status)
    {
        TimeSpan wait;
        lock (_gate)
        {
            _pendingStatus = status;
            if (_statusScheduled || _closing)
            {
                return;
            }
            wait = _lastStatus == TimeSpan.MinValue ? TimeSpan.Zero : _lastStatus + server.Options.StatusInterval - Now;
            if (wait <= TimeSpan.Zero)
            {
                FlushStatus();
                return;
            }
            _statusScheduled = true;
        }
        _ = SendStatusLaterAsync(wait);
    }

    /// <summary>Ends the connection: an optional error, then the close frame. The first close wins.</summary>
    public void Close(WebSocketCloseStatus code, string reason, ErrorMessage? error = null)
    {
        lock (_gate)
        {
            if (_closing)
            {
                return;
            }
            _closing = true;
            _deadline = null;
            if (error is not null)
            {
                _outbox.Writer.TryWrite(new Outgoing(MessageCodec.Serialize(error), null, null));
            }
            _outbox.Writer.TryWrite(new Outgoing(null, code, reason));
        }
    }

    private async Task SendStatusLaterAsync(TimeSpan wait)
    {
        try
        {
            await Task.Delay(wait, server.Timers, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        lock (_gate)
        {
            _statusScheduled = false;
            if (!_closing)
            {
                FlushStatus();
            }
        }
    }

    // Called under the lock.
    private void FlushStatus()
    {
        if (_pendingStatus is { } status)
        {
            _outbox.Writer.TryWrite(new Outgoing(status, null, null));
            (_pendingStatus, _lastStatus) = (null, Now);
        }
    }

    private async Task ReadAsync()
    {
        var assembler = new MessageAssembler();
        try
        {
            while (true)
            {
                assembler.Reset();
                AssemblyState state;
                do
                {
                    var result = await socket.ReceiveAsync(assembler.Remaining, _lifetime.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // The app closed first: answer in kind, unless we were closing already.
                        Close(CloseCodes.Normal, "closed by the app");
                        return;
                    }
                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        Close(CloseCodes.Binary, "text messages only");
                        await DrainAsync().ConfigureAwait(false);
                        return;
                    }
                    state = assembler.Advance(result.Count, result.EndOfMessage);
                }
                while (state == AssemblyState.Incomplete);
                if (state == AssemblyState.TooLarge)
                {
                    Close(CloseCodes.TooLarge, $"messages are {WireProtocol.MaxMessageBytes} bytes at most");
                    await DrainAsync().ConfigureAwait(false);
                    return;
                }
                if (!Handle(MessageCodec.ParseClient(assembler.Message)))
                {
                    await DrainAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
        {
            // The peer went without closing, or the socket was dropped: nothing more to read.
        }
    }

    /// <summary>After our close, reads until the peer's close comes back, ignoring the rest.</summary>
    private async Task DrainAsync()
    {
        var scratch = new byte[1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(scratch.AsMemory(), _lifetime.Token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }
        }
    }

    /// <summary>Acts on one message; false once the connection is closing.</summary>
    private bool Handle(MessageParse<ClientMessage> parse)
    {
        switch (parse)
        {
            case MessageParse<ClientMessage>.Parsed { Message: var message }:
                return Dispatch(message);
            case MessageParse<ClientMessage>.UnsupportedVersion:
                Close(CloseCodes.Policy, "protocol version", new ErrorMessage(ErrorCode.VersionMismatch, "This app is too new or too old for the bot. Update it."));
                return false;
            case MessageParse<ClientMessage>.TooLarge:
                Close(CloseCodes.TooLarge, $"messages are {WireProtocol.MaxMessageBytes} bytes at most");
                return false;
            default:
                // The reason names fields only, never values, but stays out of the close frame anyway.
                Close(CloseCodes.Malformed, "malformed message");
                return false;
        }
    }

    private bool Dispatch(ClientMessage message)
    {
        UserId user = default;
        lock (_gate)
        {
            if (_closing)
            {
                return false;
            }
            if (message is HelloMessage && _phase != Phase.AwaitingHello)
            {
                Close(CloseCodes.Policy, "hello sent twice");
                return false;
            }
            if (message is HelloMessage)
            {
                // Checking a code or token takes a Discord call; it gets the idle timeout to finish.
                (_phase, _deadline) = (Phase.SigningIn, Now + server.Options.IdleTimeout);
            }
            else if (_user is not { } signedIn)
            {
                // Before the welcome nothing else counts; an early heartbeat is harmless.
                return true;
            }
            else
            {
                user = signedIn;
                _deadline = Now + server.Options.IdleTimeout;
            }
        }
        if (message is HelloMessage hello)
        {
            _ = SignInAsync(hello);
            return true;
        }
        switch (message)
        {
            case PressMessage press when Advance(ref _pressSeq, press.Seq):
                server.Press(this, user, press.Action.ToCore(), press.Down);
                break;
            case OpenMicMessage openMic when Advance(ref _openMicSeq, openMic.Seq):
                server.Press(this, user, BroadcastAction.OpenMic, openMic.On);
                break;
            case HeartbeatMessage heartbeat:
                Queue(MessageCodec.Serialize(new HeartbeatAckMessage(heartbeat.Seq, heartbeat.SentAtMs)));
                if (Advance(ref _heartbeatSeq, heartbeat.Seq))
                {
                    server.Heartbeat(this, user, [.. heartbeat.Held.Select(action => action.ToCore())], heartbeat.OpenMic);
                }
                break;
            case UnlinkMessage:
                server.Devices.Revoke(_tokenHash!);
                Close(CloseCodes.Normal, "unlinked");
                return false;
            default:
                // A press or toggle older than one already seen: the app resent it.
                break;
        }
        return true;
    }

    private static bool Advance(ref long last, long seq)
    {
        if (seq <= last)
        {
            return false;
        }
        last = seq;
        return true;
    }

    private async Task SignInAsync(HelloMessage hello)
    {
        SignInResult result;
        try
        {
            result = await SignIn.RunAsync(server, hello, WaitingOnLogin, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Discord couldn't be asked: nothing is known to be wrong with the app, so it retries.
            Close(CloseCodes.ServerError, "couldn't check the sign-in; try again");
            return;
        }
        switch (result)
        {
            case SignInResult.Accepted accepted:
                var user = accepted.Member.User;
                var welcome = MessageCodec.Serialize(new WelcomeMessage(
                    user.ToWire(),
                    SignIn.DisplayName(accepted.Member),
                    WireProtocol.HeartbeatMs,
                    WireProtocol.TimeoutMs,
                    server.Options.ServerVersion,
                    accepted.NewToken));
                lock (_gate)
                {
                    if (_closing)
                    {
                        return;
                    }
                    (_user, _tokenHash, _phase, _deadline) = (user, accepted.TokenHash, Phase.SignedIn, Now + server.Options.IdleTimeout);
                }
                server.Register(this, user, welcome);
                bool ended;
                lock (_gate)
                {
                    (_registered, ended) = (true, _ended);
                }
                if (ended)
                {
                    server.Unregister(this, user);
                }
                break;
            case SignInResult.Refused refused:
                Close(CloseCodes.Policy, "sign-in refused", refused.Error);
                break;
        }
    }

    /// <summary>A Discord sign-in waits on the player in the browser, up to the login's own expiry.</summary>
    private void WaitingOnLogin(string loginHash)
    {
        lock (_gate)
        {
            (_loginHash, _phase, _deadline) = (loginHash, Phase.WaitingOnLogin, null);
        }
    }

    private async Task WriteAsync()
    {
        try
        {
            await foreach (var item in _outbox.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (item.Message is { } message)
                {
                    await socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, _lifetime.Token).ConfigureAwait(false);
                    continue;
                }
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await socket.CloseOutputAsync(item.Code!.Value, item.Reason, _lifetime.Token).ConfigureAwait(false);
                }
                // The peer has the grace to answer; then the socket goes regardless.
                await Task.WhenAny(_reading, Task.Delay(server.Options.CloseGrace, server.Timers, _lifetime.Token)).ConfigureAwait(false);
                if (!_reading.IsCompleted)
                {
                    socket.Abort();
                }
                return;
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The socket went; the reader sees it too.
        }
    }

    private async Task WatchAsync()
    {
        try
        {
            while (true)
            {
                TimeSpan? deadline;
                Phase phase;
                lock (_gate)
                {
                    (deadline, phase) = (_deadline, _phase);
                }
                var wait = deadline is { } d ? d - Now : TimeSpan.FromMilliseconds(250);
                if (wait <= TimeSpan.Zero)
                {
                    Close(phase == Phase.SigningIn ? CloseCodes.ServerError : CloseCodes.Policy, phase switch
                    {
                        Phase.AwaitingHello => "no hello",
                        Phase.SigningIn => "couldn't check the sign-in; try again",
                        _ => "no heartbeat",
                    });
                    return;
                }
                await Task.Delay(wait, server.Timers, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The connection ended.
        }
    }

    private enum Phase
    {
        AwaitingHello,
        SigningIn,
        WaitingOnLogin,
        SignedIn,
    }

    /// <summary>A message to send, or the close to end with.</summary>
    private sealed record Outgoing(byte[]? Message, WebSocketCloseStatus? Code, string? Reason);
}
