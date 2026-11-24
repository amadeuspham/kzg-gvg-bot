using Kzg.App.Link;
using Kzg.Protocol;

namespace Kzg.App.Services;

/// <summary>How the stand-in server behaves in <c>--demo</c>.</summary>
public enum DemoMode
{
    /// <summary>Welcomes the app, acknowledges heartbeats, and says the team key reaches two voice channels and the all-teams key five.</summary>
    Online,

    /// <summary>Never reachable: shows Reconnecting, then Disconnected.</summary>
    Offline,

    /// <summary>Online for 10 seconds, then gone for a minute, then back.</summary>
    Flaky,
}

/// <summary>
/// A stand-in bot for looking at the window without a server (<c>--demo</c>). It speaks the real wire
/// through <see cref="MessageCodec"/>, so the real connection controller runs against it.
/// </summary>
public sealed class DemoTransportFactory(DemoMode mode, Action<Action> post) : ILinkTransportFactory
{
    private DateTime? _firstConnect;

    public ILinkTransport Create() => new DemoTransport(mode, post, () => _firstConnect ??= DateTime.UtcNow);

    private sealed class DemoTransport(DemoMode mode, Action<Action> post, Func<DateTime> firstConnect) : ILinkTransport
    {
        private bool _disposed;
        private readonly HashSet<WireAction> _pressed = [];
        private bool _openMic;

        public event Action<ReadOnlyMemory<byte>>? Received;

        public event Action<int?>? Closed;

        public Task ConnectAsync(Uri uri, CancellationToken cancellation)
        {
            if (mode == DemoMode.Offline)
            {
                throw new HttpRequestException("demo: unreachable");
            }
            var age = DateTime.UtcNow - firstConnect();
            if (mode == DemoMode.Flaky && age > TimeSpan.FromSeconds(10) && age < TimeSpan.FromSeconds(70))
            {
                throw new HttpRequestException("demo: unreachable");
            }
            if (mode == DemoMode.Flaky && age < TimeSpan.FromSeconds(1))
            {
                _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => post(() =>
                {
                    if (!_disposed)
                    {
                        Closed?.Invoke(null);
                    }
                }));
            }
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> message)
        {
            if (MessageCodec.ParseClient(message.Span) is not MessageParse<ClientMessage>.Parsed { Message: var client })
            {
                return Task.CompletedTask;
            }
            switch (client)
            {
                case HelloMessage:
                    Reply(new WelcomeMessage("1", "Demo Player", 2000, 6000, "demo", WireSecret.Generate()));
                    ReplyStatus();
                    break;
                case PressMessage press:
                    if (press.Down)
                    {
                        _pressed.Add(press.Action);
                    }
                    else
                    {
                        _pressed.Remove(press.Action);
                    }
                    ReplyStatus();
                    break;
                case OpenMicMessage openMic:
                    _openMic = openMic.On;
                    ReplyStatus();
                    break;
                case HeartbeatMessage heartbeat:
                    Reply(new HeartbeatAckMessage(heartbeat.Seq, heartbeat.SentAtMs));
                    break;
            }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }

        private void ReplyStatus()
        {
            List<WireAction> active = [.. new[] { WireAction.Group, WireAction.AllTeams }.Where(_pressed.Contains)];
            if (_openMic)
            {
                active.Add(WireAction.OpenMic);
            }
            List<string> targets = _pressed.Contains(WireAction.AllTeams)
                ? ["Attack 2", "Attack 3", "Defense 1", "Defense 2", "Defense 3"]
                : ["Attack 2", "Attack 3"];
            Reply(new StatusMessage(active, _openMic, targets, [], "Attack 1", WireGroup.Attack));
        }

        private void Reply(ServerMessage message)
        {
            var bytes = MessageCodec.Serialize(message);
            post(() => Received?.Invoke(bytes));
        }
    }
}
