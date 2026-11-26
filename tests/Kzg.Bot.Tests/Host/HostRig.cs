using System.Net;
using System.Net.WebSockets;
using System.Text;
using Kzg.Audio;
using Kzg.Bot.Fake;
using Kzg.Bot.Http;
using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Core.Pairing;
using Kzg.Protocol;
using Microsoft.Extensions.Logging;

namespace Kzg.Bot.Tests.Hosting;

/// <summary>The bot host over the demo guild on a loopback port of its own, with its log kept in memory.</summary>
internal sealed class HostRig : IAsyncDisposable
{
    public const ulong ClientId = 333333333333333333;

    private readonly string _folder;
    private readonly bool _ownsFolder;

    public HostRig(Func<BotConfig, BotConfig>? config = null, string? folder = null, SignInIdentity? identity = null, HttpLimits? limits = null)
    {
        (_folder, _ownsFolder) = (folder ?? Path.Combine(Path.GetTempPath(), "kzg-host-tests", Guid.NewGuid().ToString("N")), folder is null);
        World = DemoWorld.Create(Time, "gvg.example.com");
        Identity = identity ?? new SignInIdentity(DemoWorld.Members);
        var configured = World.Config with
        {
            OAuth = new OAuthConfig { Enabled = true, ClientId = ClientId },
        };
        Bot = BotApp.Create(new BotSetup
        {
            Config = (config ?? (c => c))(configured),
            Secrets = Secrets.Combine(new Dictionary<string, string> { ["relay1"] = BotToken }, new Dictionary<string, string>()),
            DataFolder = _folder,
            Gateway = World.Gateway,
            Discord = Identity,
            Codecs = new ConcentusOpusCodecFactory(),
            ChannelNames = World.Names,
            Time = Time,
            Listen = new IPEndPoint(IPAddress.Loopback, 0),
            Log = Log,
            LogLevel = LogLevel.Trace,
            QuietFramework = false,
            Limits = limits ?? new HttpLimits(),
        });
    }

    // Made up at run time; the log must never show it.
    public static string BotToken { get; } = string.Join('.', new string('A', 24), "G1x2y3", new string('b', 27));

    public SystemTimeSource Time { get; } = new();

    public StringWriter Log { get; } = new();

    public DemoWorld World { get; }

    public SignInIdentity Identity { get; }

    public BotApp Bot { get; }

    public string Folder => _folder;

    public HttpClient Http { get; } = new(new HttpClientHandler { AllowAutoRedirect = false });

    public string LogText
    {
        get
        {
            lock (Log)
            {
                return Log.ToString();
            }
        }
    }

    public async Task<HostRig> StartAsync()
    {
        World.SeatPlayers();
        await Bot.StartAsync();
        return this;
    }

    public Uri Url(string path) => new(Bot.Address, path);

    public async Task<AppClient> ConnectAsync()
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new UriBuilder(Bot.Address) { Scheme = "ws", Path = "/ws" }.Uri, CancellationToken.None);
        return new AppClient(socket);
    }

    /// <summary>An app signed in as <paramref name="user"/> by a fresh connect code; returns it with its welcome.</summary>
    public async Task<(AppClient App, WelcomeMessage Welcome)> SignInAsync(UserId user)
    {
        var app = await ConnectAsync();
        await app.SendAsync(new HelloMessage("1.0.0", Guid.NewGuid(), Code: Bot.Codes.Issue(user, $"127.0.0.1:{Bot.Address.Port}")));
        return (app, await app.ExpectAsync<WelcomeMessage>());
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await Bot.DisposeAsync();
        if (_ownsFolder && Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}

/// <summary>Discord sign-in as a table: one OAuth code that signs Alice in, and the guild's members.</summary>
internal sealed class SignInIdentity(IReadOnlyDictionary<UserId, string> members) : IDiscordIdentity
{
    public const string GoodCode = "discord-says-alice";

    public Task<UserId?> ExchangeCodeAsync(string code, Uri redirectUri, CancellationToken cancellationToken) =>
        Task.FromResult<UserId?>(code == GoodCode ? DemoWorld.Alice : null);

    public Task<GuildMember?> GetMemberAsync(UserId user, CancellationToken cancellationToken) =>
        Task.FromResult(members.TryGetValue(user, out var name) ? new GuildMember(user, name) : null);
}

/// <summary>The PC app's end of a real WebSocket: sends messages, and reads what the bot says in order.</summary>
internal sealed class AppClient(ClientWebSocket socket) : IAsyncDisposable
{
    public ClientWebSocket Socket { get; } = socket;

    public Task SendAsync(ClientMessage message) =>
        Socket.SendAsync(MessageCodec.Serialize(message), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>The next message, or null once the bot closed the connection; <see cref="CloseStatus"/> then says how.</summary>
    public async Task<ServerMessage?> ReceiveAsync(TimeSpan? within = null)
    {
        using var timeout = new CancellationTokenSource(within ?? TimeSpan.FromSeconds(10));
        var buffer = new byte[8192];
        var length = 0;
        while (true)
        {
            var result = await Socket.ReceiveAsync(buffer.AsMemory(length), timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                try
                {
                    if (Socket.State == WebSocketState.CloseReceived)
                    {
                        await Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    }
                }
                catch (WebSocketException)
                {
                    // The bot's grace ran out first; its close is what counts.
                }
                return null;
            }
            length += result.Count;
            if (result.EndOfMessage)
            {
                return Assert.IsType<MessageParse<ServerMessage>.Parsed>(MessageCodec.ParseServer(buffer.AsSpan(0, length))).Message;
            }
        }
    }

    public WebSocketCloseStatus? CloseStatus => Socket.CloseStatus;

    /// <summary>Reads until the bot closes the connection.</summary>
    public async Task UntilClosedAsync()
    {
        while (await ReceiveAsync() is not null)
        {
        }
    }

    public async Task<T> ExpectAsync<T>()
        where T : ServerMessage =>
        Assert.IsType<T>(await ReceiveAsync());

    /// <summary>Reads until a message of this type satisfies <paramref name="match"/>, returning it; everything before is in <paramref name="seen"/>.</summary>
    public async Task<T> UntilAsync<T>(Func<T, bool> match, List<ServerMessage>? seen = null, TimeSpan? within = null)
        where T : ServerMessage
    {
        var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            var message = await ReceiveAsync(deadline - DateTime.UtcNow) ?? throw new InvalidOperationException("the bot closed the connection");
            seen?.Add(message);
            if (message is T typed && match(typed))
            {
                return typed;
            }
        }
        throw new TimeoutException($"no matching {typeof(T).Name}");
    }

    public async ValueTask DisposeAsync()
    {
        Socket.Abort();
        Socket.Dispose();
        await Task.CompletedTask;
    }
}
