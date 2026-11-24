using System.Net.WebSockets;
using Kzg.Core.Abstractions;
using Kzg.Core.Pairing;
using Kzg.Protocol;

namespace Kzg.Bot.Control;

/// <summary>
/// The <c>/ws</c> side of the bot: signs apps in by connect code, device token or Discord sign-in,
/// then turns their presses, toggles and heartbeats into engine input and sends them their status.
/// One app per player: a newer sign-in closes the older connection. The host accepts each WebSocket
/// and hands it to <see cref="RunAsync"/>; everything else here is thread-safe.
/// </summary>
public sealed class ControlServer : IBroadcastTrigger
{
    private readonly Dictionary<ulong, Connection> _current = [];
    private readonly HashSet<Connection> _open = [];
    private readonly Lock _gate = new();
    private readonly IAppEvents _events;

    /// <param name="time">The engine's clock, for the times on edges and heartbeats.</param>
    /// <param name="timers">Real time for deadlines; tests may pass their own.</param>
    public ControlServer(
        ControlOptions options,
        ITimeSource time,
        TimeProvider timers,
        ConnectCodes codes,
        PendingLogins logins,
        DeviceRegistry devices,
        IDiscordIdentity discord,
        IAppEvents events)
    {
        (Options, Time, Timers, Codes, Logins, Devices, Discord) = (options, time, timers, codes, logins, devices, discord);
        _events = events;
    }

    /// <summary>A press or an open-mic toggle from a signed-in app, as a claim routing checks.</summary>
    public event Action<BroadcastEdge> Edge = delegate { };

    internal ControlOptions Options { get; }

    internal ITimeSource Time { get; }

    internal TimeProvider Timers { get; }

    internal ConnectCodes Codes { get; }

    internal PendingLogins Logins { get; }

    internal DeviceRegistry Devices { get; }

    internal IDiscordIdentity Discord { get; }

    /// <summary>Players with a signed-in app now.</summary>
    public IReadOnlyList<UserId> SignedIn
    {
        get
        {
            lock (_gate)
            {
                return [.. _current.Keys.Order().Select(id => new UserId(id))];
            }
        }
    }

    /// <summary>Serves one accepted WebSocket until it closes.</summary>
    public async Task RunAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var connection = new Connection(this, socket);
        lock (_gate)
        {
            _open.Add(connection);
        }
        try
        {
            await connection.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _open.Remove(connection);
            }
        }
    }

    /// <summary>
    /// Sends a player's app its status, if it is signed in. A burst is coalesced: the app gets the
    /// newest, at most 10 a second. Throws for a status that breaks the protocol's rules.
    /// </summary>
    public void SendStatus(UserId user, StatusMessage status)
    {
        var bytes = MessageCodec.Serialize(status);
        lock (_gate)
        {
            if (_current.TryGetValue(user.V, out var connection))
            {
                connection.QueueStatus(bytes);
            }
        }
    }

    /// <summary>
    /// <c>/gvg revoke user</c>: forgets every device token of the player and closes their app at
    /// once, with <c>revoked</c>. Returns how many tokens went.
    /// </summary>
    public int RevokeUser(UserId user)
    {
        var count = Devices.RevokeUser(user);
        lock (_gate)
        {
            if (_current.TryGetValue(user.V, out var connection))
            {
                connection.Close(CloseCodes.Policy, "revoked", new ErrorMessage(ErrorCode.Revoked, "This app was unlinked by an admin. Link it again to use it."));
            }
        }
        return count;
    }

    /// <summary>Closes every connection, as the bot shuts down.</summary>
    public void CloseAll()
    {
        lock (_gate)
        {
            foreach (var connection in _open)
            {
                connection.Close(CloseCodes.GoingAway, "server shutting down");
            }
        }
    }

    /// <summary>
    /// Makes <paramref name="connection"/> its player's app: closes an older one, queues the welcome
    /// ahead of anything else, then tells the engine, all under one lock, so the engine never hears
    /// the old app after the new one.
    /// </summary>
    internal void Register(Connection connection, UserId user, byte[] welcome)
    {
        lock (_gate)
        {
            if (_current.TryGetValue(user.V, out var older) && older != connection)
            {
                older.Close(CloseCodes.Replaced, "signed in from another app");
            }
            _current[user.V] = connection;
            connection.Queue(welcome);
            _events.Connected(user, Time.Now);
        }
    }

    internal void Unregister(Connection connection, UserId user)
    {
        lock (_gate)
        {
            if (_current.TryGetValue(user.V, out var current) && current == connection)
            {
                _current.Remove(user.V);
                _events.Disconnected(user);
            }
        }
    }

    /// <summary>Raises an edge, unless a newer app of the player has taken over.</summary>
    internal void Press(Connection connection, UserId user, BroadcastAction action, bool on)
    {
        lock (_gate)
        {
            if (IsCurrent(connection, user))
            {
                Edge(new BroadcastEdge(user, action, on, TriggerKind.AppKey, Time.Now));
            }
        }
    }

    internal void Heartbeat(Connection connection, UserId user, IReadOnlyList<BroadcastAction> held, bool openMic)
    {
        lock (_gate)
        {
            if (IsCurrent(connection, user))
            {
                _events.Heartbeat(user, held, openMic, Time.Now);
            }
        }
    }

    private bool IsCurrent(Connection connection, UserId user) =>
        _current.TryGetValue(user.V, out var current) && current == connection;
}
