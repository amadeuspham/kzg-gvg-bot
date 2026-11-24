using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using Kzg.Protocol;

namespace Kzg.Bot.Tests.Live;

/// <summary>The PC app, as far as the bot can tell: a WebSocket that says hello with a code, holds the key and sends heartbeats.</summary>
internal sealed class LiveAppClient(Stopwatch? clock = null) : IAsyncDisposable
{
    private readonly Stopwatch _clock = clock ?? Stopwatch.StartNew();
    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sending = new(1, 1);
    private long _seq;
    private bool _held;
    private bool _allTeamsHeld;
    private bool _openMic;
    private volatile bool _unplugged;
    private Task _reading = Task.CompletedTask;
    private Task _beating = Task.CompletedTask;

    public ConcurrentQueue<ServerMessage> Received { get; } = new();

    /// <summary>Every status the bot sent, with when it arrived (the test's clock, in ms).</summary>
    public ConcurrentQueue<(double At, StatusMessage Status)> Statuses { get; } = new();

    /// <summary>When the last heartbeat went out, on the test's clock in ms.</summary>
    public double LastBeatAt { get; private set; }

    public WelcomeMessage? Welcome { get; private set; }

    public StatusMessage? LastStatus => Received.OfType<StatusMessage>().LastOrDefault();

    public async Task ConnectAsync(Uri address, string code)
    {
        await _socket.ConnectAsync(new UriBuilder(address) { Scheme = "ws", Path = "/ws" }.Uri, _stop.Token);
        _reading = Task.Run(ReadAsync);
        await SendAsync(new HelloMessage("live-check", Guid.NewGuid(), Code: code));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (Welcome is null)
        {
            if (DateTime.UtcNow > deadline || Received.OfType<ErrorMessage>().FirstOrDefault() is not null)
            {
                throw new InvalidOperationException("no welcome: " + string.Join("; ", Received.Select(m => m.Type)));
            }
            await Task.Delay(20);
        }
        _beating = Task.Run(BeatAsync);
    }

    /// <summary>The open-mic toggle, as the app sends it.</summary>
    public async Task OpenMicAsync(bool on)
    {
        await _sending.WaitAsync(_stop.Token);
        try
        {
            lock (_gate)
            {
                _openMic = on;
            }
            await SendRawAsync(new OpenMicMessage(on, Interlocked.Increment(ref _seq)));
        }
        finally
        {
            _sending.Release();
        }
    }

    /// <summary>Waits until the newest status the bot sent satisfies <paramref name="match"/>.</summary>
    public async Task<StatusMessage> WaitStatusAsync(Func<StatusMessage, bool> match, TimeSpan? within = null)
    {
        var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            if (LastStatus is { } status && match(status))
            {
                return status;
            }
            await Task.Delay(10);
        }
        var last = LastStatus;
        throw new TimeoutException("no status like that; the last was " + (last is null ? "none" : $"{last.ChannelName} [{string.Join(",", last.Active)}] open mic {last.OpenMic} to [{string.Join(", ", last.Targets)}]"));
    }

    /// <summary>The network goes: nothing more is sent, and the connection stays open.</summary>
    public void Unplug() => _unplugged = true;

    /// <summary>The connection is cut without a word.</summary>
    public void Sever()
    {
        _unplugged = true;
        _socket.Abort();
    }

    /// <summary>The team key, or with <paramref name="allTeams"/> the all-teams key.</summary>
    public async Task PressAsync(bool down, bool allTeams = false)
    {
        // One message at a time, and the key state read as it goes out: a heartbeat made before a press but
        // sent after it would say the key is up, and the bot would believe it.
        await _sending.WaitAsync(_stop.Token);
        try
        {
            lock (_gate)
            {
                if (allTeams)
                {
                    _allTeamsHeld = down;
                }
                else
                {
                    _held = down;
                }
            }
            await SendRawAsync(new PressMessage(allTeams ? WireAction.AllTeams : WireAction.Group, down, Interlocked.Increment(ref _seq)));
        }
        finally
        {
            _sending.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
        {
            // The bot may have closed first.
        }
        await Task.WhenAll(_reading.ContinueWith(_ => { }), _beating.ContinueWith(_ => { }));
        _socket.Dispose();
        _stop.Dispose();
    }

    private async Task SendAsync(ClientMessage message)
    {
        await _sending.WaitAsync(_stop.Token);
        try
        {
            await SendRawAsync(message);
        }
        finally
        {
            _sending.Release();
        }
    }

    private async Task SendRawAsync(ClientMessage message)
    {
        if (_socket.State == WebSocketState.Open && !_unplugged)
        {
            await _socket.SendAsync(MessageCodec.Serialize(message), WebSocketMessageType.Text, true, _stop.Token);
        }
    }

    private async Task BeatAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            await Task.Delay(2000, _stop.Token);
            await _sending.WaitAsync(_stop.Token);
            try
            {
                bool held, allTeamsHeld, openMic;
                lock (_gate)
                {
                    (held, allTeamsHeld, openMic) = (_held, _allTeamsHeld, _openMic);
                }
                if (!_unplugged)
                {
                    LastBeatAt = _clock.Elapsed.TotalMilliseconds;
                }
                await SendRawAsync(new HeartbeatMessage(Interlocked.Increment(ref _seq), [.. held ? [WireAction.Group] : Array.Empty<WireAction>(), .. allTeamsHeld ? [WireAction.AllTeams] : Array.Empty<WireAction>()], openMic, Environment.TickCount64));
            }
            finally
            {
                _sending.Release();
            }
        }
    }

    private async Task ReadAsync()
    {
        var buffer = new byte[4096];
        try
        {
            while (!_stop.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var result = await _socket.ReceiveAsync(buffer, _stop.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
                if (MessageCodec.ParseServer(buffer.AsSpan(0, result.Count)) is MessageParse<ServerMessage>.Parsed parsed)
                {
                    Received.Enqueue(parsed.Message);
                    if (parsed.Message is StatusMessage status)
                    {
                        Statuses.Enqueue((_clock.Elapsed.TotalMilliseconds, status));
                    }
                    if (parsed.Message is WelcomeMessage welcome)
                    {
                        Welcome = welcome;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
        {
            // Closed.
        }
    }
}
