using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Kzg.Bot.Control;
using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Core.Pairing;
using Kzg.Protocol;

namespace Kzg.Bot.Tests.Control;

/// <summary>The engine's clock, moved by hand.</summary>
internal sealed class ManualTime : ITimeSource
{
    public TimeSpan Now { get; set; } = TimeSpan.FromHours(5);
}

/// <summary>
/// Timestamps and timers that move only on <see cref="Advance"/>, which fires every timer then due, in order,
/// on the caller's thread. A test of a rate then reads what is sent at each instant, not how fast the machine is.
/// </summary>
internal sealed class ManualTimers : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<Timer> _timers = [];
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _ticks;
        }
    }

    /// <summary>How many timers are waiting to fire.</summary>
    public int Waiting
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(t => t.Due is not null);
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        long end;
        lock (_gate)
        {
            end = _ticks + by.Ticks;
        }
        while (true)
        {
            Timer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due <= end).MinBy(t => t.Due);
                if (next is null)
                {
                    _ticks = end;
                    return;
                }
                _ticks = Math.Max(_ticks, next.Due!.Value);
                next.Due = next.Period is { } period ? _ticks + period : null;
            }
            next.Fire();
        }
    }

    private sealed class Timer(ManualTimers owner, TimerCallback callback, object? state) : ITimer
    {
        public long? Due { get; set; }

        public long? Period { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._ticks + Math.Max(0, dueTime.Ticks);
                Period = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : period.Ticks;
                owner._timers.Add(this);
            }
            return true;
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                Due = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}

internal sealed class ManualWallClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 23, 20, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Discord as the tests want it: who is a member, and which OAuth code signs in whom.</summary>
internal sealed class FakeDiscord : IDiscordIdentity
{
    public ConcurrentDictionary<ulong, string> Members { get; } = new();

    public ConcurrentDictionary<string, UserId> Codes { get; } = new();

    public bool Unreachable { get; set; }

    public List<Uri> RedirectUris { get; } = [];

    public Task<UserId?> ExchangeCodeAsync(string code, Uri redirectUri, CancellationToken cancellationToken)
    {
        lock (RedirectUris)
        {
            RedirectUris.Add(redirectUri);
        }
        return Unreachable ? throw new HttpRequestException("unreachable")
            : Task.FromResult<UserId?>(Codes.TryGetValue(code, out var user) ? user : null);
    }

    public async Task<GuildMember?> GetMemberAsync(UserId user, CancellationToken cancellationToken)
    {
        await Task.Yield();
        return Unreachable ? throw new HttpRequestException("unreachable")
            : Members.TryGetValue(user.V, out var name) ? new GuildMember(user, name) : null;
    }
}

/// <summary>Every app event, in order, as text.</summary>
internal sealed class RecordingEvents : IAppEvents
{
    private readonly ConcurrentQueue<string> _log = new();

    public IReadOnlyList<string> Log => [.. _log];

    public void Connected(UserId user, TimeSpan at) => _log.Enqueue($"connected {user}");

    public void Disconnected(UserId user) => _log.Enqueue($"disconnected {user}");

    public void Heartbeat(UserId user, IReadOnlyList<BroadcastAction> held, bool openMic, TimeSpan at) =>
        _log.Enqueue($"heartbeat {user} [{string.Join(",", held)}] {openMic}");

    public void Add(string entry) => _log.Enqueue(entry);
}

/// <summary>What the app side read: a message, or the close that ended the connection.</summary>
internal sealed record Received(ServerMessage? Message, WebSocketCloseStatus? Close, string? Reason);

/// <summary>An app's end of a real WebSocket over a real loopback socket.</summary>
internal sealed class TestApp(WebSocket socket, Task serverRun) : IAsyncDisposable
{
    public WebSocket Socket { get; } = socket;

    /// <summary>The server's side of this connection; done once the server let it go.</summary>
    public Task ServerRun { get; } = serverRun;

    public Task SendAsync(ClientMessage message) => SendRawAsync(MessageCodec.Serialize(message));

    public Task SendRawAsync(string text) => SendRawAsync(Encoding.UTF8.GetBytes(text));

    public async Task SendRawAsync(byte[] bytes, WebSocketMessageType type = WebSocketMessageType.Text) =>
        await Socket.SendAsync(bytes, type, endOfMessage: true, CancellationToken.None);

    public async Task<Received> ReceiveAsync(TimeSpan? within = null)
    {
        using var timeout = new CancellationTokenSource(within ?? TimeSpan.FromSeconds(10));
        var buffer = new byte[8192];
        var length = 0;
        while (true)
        {
            var result = await Socket.ReceiveAsync(buffer.AsMemory(length), timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (Socket.State == WebSocketState.CloseReceived)
                {
                    await Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                }
                return new Received(null, Socket.CloseStatus, Socket.CloseStatusDescription);
            }
            length += result.Count;
            if (result.EndOfMessage)
            {
                var parse = MessageCodec.ParseServer(buffer.AsSpan(0, length));
                return new Received(Assert.IsType<MessageParse<ServerMessage>.Parsed>(parse).Message, null, null);
            }
        }
    }

    public async Task<T> ExpectAsync<T>()
        where T : ServerMessage
    {
        var received = await ReceiveAsync();
        return Assert.IsType<T>(received.Message);
    }

    /// <summary>Reads past any messages to the close, and waits for the server to let the connection go.</summary>
    public async Task<(WebSocketCloseStatus? Status, string? Reason, List<ServerMessage> Before)> ExpectCloseAsync()
    {
        List<ServerMessage> before = [];
        while (true)
        {
            var received = await ReceiveAsync();
            if (received.Message is null)
            {
                await ServerRun.WaitAsync(TimeSpan.FromSeconds(10));
                return (received.Close, received.Reason, before);
            }
            before.Add(received.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Socket.Abort();
        Socket.Dispose();
        try
        {
            await ServerRun.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            // The test fails on its own assertions; a stuck server side is not hidden from it.
            throw;
        }
    }
}

/// <summary>A control server with real pairing over a temp folder, a fake Discord and a hand-moved clock.</summary>
internal sealed class Harness : IAsyncDisposable
{
    public const string Host = "gvg.example.com";

    public static readonly UserId Alice = new(1), Bob = new(2), Stranger = new(9);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kzg-bot-tests", Guid.NewGuid().ToString("N"));
    private readonly List<TestApp> _apps = [];

    /// <param name="timers">The server's timers and timestamps; the real ones unless a test moves them by hand.</param>
    public Harness(ControlOptions? options = null, string? folder = null, TimeProvider? timers = null)
    {
        _folder = folder ?? _folder;
        Directory.CreateDirectory(_folder);
        Store = new DevicesStore(Path.Combine(_folder, "devices.json"));
        Discord.Members[Alice.V] = "Alice";
        Discord.Members[Bob.V] = "Боб";
        Codes = new ConnectCodes(Time);
        Logins = new PendingLogins(Time);
        Devices = DeviceRegistry.Over(Store, Assert.IsType<FileLoad<IReadOnlyList<DeviceRecord>>.Loaded>(Store.Load()).Value, Wall, TimeSpan.FromDays(90));
        Server = new ControlServer(options ?? new ControlOptions { ServerVersion = "0.1.0" }, Time, timers ?? TimeProvider.System, Codes, Logins, Devices, Discord, Events);
        Server.Edge += edge => Events.Add($"edge {edge.User} {edge.Action} {edge.On} {edge.Kind}");
        Auth = new AuthEndpoints(Logins, Discord, () => SignInOn ? new SignInSettings(333333333333333333, Host) : null);
    }

    public string Folder => _folder;

    public ManualTime Time { get; } = new();

    public ManualWallClock Wall { get; } = new();

    public FakeDiscord Discord { get; } = new();

    public RecordingEvents Events { get; } = new();

    public DevicesStore Store { get; }

    public ConnectCodes Codes { get; }

    public PendingLogins Logins { get; }

    public DeviceRegistry Devices { get; }

    public ControlServer Server { get; }

    public AuthEndpoints Auth { get; }

    public bool SignInOn { get; set; } = true;

    public static HelloMessage Hello(string? code = null, string? token = null, string? login = null, Guid? install = null) =>
        new("1.0.0", install ?? Guid.Parse("6f1c2a4e-0000-4000-8000-000000000001"), code, token, login);

    public async Task<TestApp> ConnectAsync(CancellationToken stop = default)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connecting = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var accepted = await listener.AcceptTcpClientAsync();
        await connecting;
        var serverSocket = WebSocket.CreateFromStream(accepted.GetStream(), new WebSocketCreationOptions { IsServer = true });
        var appSocket = WebSocket.CreateFromStream(client.GetStream(), new WebSocketCreationOptions { IsServer = false });
        var run = Task.Run(async () =>
        {
            try
            {
                await Server.RunAsync(serverSocket, stop);
            }
            finally
            {
                serverSocket.Dispose();
                accepted.Dispose();
            }
        });
        var app = new TestApp(appSocket, run);
        _apps.Add(app);
        return app;
    }

    /// <summary>Signs <paramref name="user"/> in with a fresh connect code and returns the app and its welcome.</summary>
    public async Task<(TestApp App, WelcomeMessage Welcome)> SignInAsync(UserId user, Guid? install = null)
    {
        var app = await ConnectAsync();
        await app.SendAsync(Hello(code: Codes.Issue(user, Host), install: install));
        return (app, await app.ExpectAsync<WelcomeMessage>());
    }

    /// <summary>Waits until the event log holds <paramref name="entry"/>.</summary>
    public async Task WaitForEventAsync(string entry)
    {
        for (var i = 0; i < 200 && !Events.Log.Contains(entry); i++)
        {
            await Task.Delay(25);
        }
        Assert.Contains(entry, Events.Log);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
        {
            await app.DisposeAsync();
        }
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
