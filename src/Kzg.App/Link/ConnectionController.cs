using Kzg.Core.Abstractions;
using Kzg.Protocol;

namespace Kzg.App.Link;

/// <summary>The timings of the link. Tests and the real app use the same values.</summary>
public sealed record ConnectionOptions
{
    /// <summary>How long a dropped link shows Reconnecting before the badge says Disconnected.</summary>
    public TimeSpan ReconnectingFor { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long a connection may go without <c>welcome</c> after the socket opens.</summary>
    public TimeSpan WelcomeDeadline { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The pause before each new attempt; the last one repeats.</summary>
    public IReadOnlyList<TimeSpan> Backoff { get; init; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    /// <summary>How long a Discord sign-in may take; the bot forgets the login id after 10 minutes.</summary>
    public TimeSpan SignInLifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Dev only: a loopback address to connect to instead of <c>wss://&lt;linked host&gt;/ws</c>.</summary>
    public Uri? ServerOverride { get; init; }

    /// <summary>A status that says open mic is off is ignored this long after the app asked for it on.</summary>
    public TimeSpan OpenMicGrace { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// The app's side of <c>/ws</c>: connects, says hello, heartbeats, reports key edges, and reconnects
/// with the device token after a drop. All its time comes from <see cref="ITimeSource"/> and
/// <see cref="Tick"/>, so a test moves it with a fake clock. Not thread-safe: call it, and let the
/// transport call it, on one thread (the UI thread). Never blocks.
/// </summary>
public sealed class ConnectionController(
    ILinkTransportFactory transports,
    ITimeSource time,
    Guid installId,
    string appVersion,
    ConnectionOptions? options = null)
{
    private readonly ConnectionOptions _options = options ?? new ConnectionOptions();

    private LinkCredential? _credential;
    private string _host = "";
    private int _generation;
    private ILinkTransport? _transport;
    private CancellationTokenSource? _connecting;
    private LinkPhase _phase = LinkPhase.NotLinked;
    private LinkFailure _failure = LinkFailure.NotLinked;
    private LinkNotice _notice;
    private bool _helloSent;
    private int _failures;
    private TimeSpan _attemptStartedAt;
    private TimeSpan _nextAttemptAt;
    private TimeSpan _signInStartedAt;
    private TimeSpan? _offlineSince;
    private TimeSpan _lastReceivedAt;
    private TimeSpan _nextHeartbeatAt;
    private TimeSpan _heartbeat = TimeSpan.FromMilliseconds(WireProtocol.HeartbeatMs);
    private TimeSpan _timeout = TimeSpan.FromMilliseconds(WireProtocol.TimeoutMs);
    private TimeSpan _openMicAskedAt;
    private long _seq;
    private long _heartbeatSeq;
    private bool _groupHeld;
    private bool _allTeamsHeld;
    private bool _openMic;
    private string? _displayName;
    private StatusMessage? _status;
    private int? _roundTripMs;
    private ConnectionSnapshot _snapshot = ConnectionSnapshot.NotLinked;

    public event Action<ConnectionSnapshot>? Changed;

    /// <summary>The bot issued a new device token. Save it: it is how every later start signs in.</summary>
    public event Action<string>? DeviceTokenIssued;

    /// <summary>The bot will never accept the saved device token again (revoked or expired). Forget it.</summary>
    public event Action? TokenRejected;

    public ConnectionSnapshot Snapshot => _snapshot;

    /// <summary>Dev only: the address comes from <c>--server</c>, so a linked host name isn't needed to connect.</summary>
    public bool HasServerOverride => _options.ServerOverride is not null;

    /// <summary>Starts linking or reconnecting with <paramref name="credential"/>, replacing any earlier attempt.</summary>
    public void Start(LinkCredential credential, string host)
    {
        ArgumentNullException.ThrowIfNull(credential);
        Teardown();
        _credential = credential;
        _host = host;
        _failures = 0;
        _failure = LinkFailure.None;
        _notice = LinkNotice.None;
        _signInStartedAt = time.Now;
        _offlineSince = time.Now;
        _phase = LinkPhase.Connecting;
        BeginAttempt();
    }

    /// <summary>Stops everything and forgets the credential: the player is now unlinked here.</summary>
    public void Stop()
    {
        Teardown();
        _credential = null;
        _phase = LinkPhase.NotLinked;
        _failure = LinkFailure.NotLinked;
        _notice = LinkNotice.None;
        _offlineSince = null;
        Publish();
    }

    /// <summary>Skips the pause between attempts and tries now. Does nothing unless the link is waiting for its next attempt.</summary>
    public void RetryNow()
    {
        if (_phase == LinkPhase.Offline)
        {
            BeginAttempt();
        }
    }

    /// <summary>Tells the bot to revoke this device token, then stops. Offline, it only stops.</summary>
    public async Task UnlinkAsync()
    {
        var transport = _phase == LinkPhase.Online ? _transport : null;
        if (transport is not null)
        {
            try
            {
                await transport.SendAsync(MessageCodec.Serialize(new UnlinkMessage()));
            }
            catch (Exception)
            {
                // The token stays valid on the bot until it expires or an admin revokes it.
            }
        }
        Stop();
    }

    /// <summary>The team key went down or up on this PC. It is remembered while offline and restated on reconnect.</summary>
    public void SetGroupKey(bool down) => SetKey(ref _groupHeld, WireAction.Group, down);

    /// <summary>The all-teams key went down or up on this PC. It is remembered while offline and restated on reconnect.</summary>
    public void SetAllTeamsKey(bool down) => SetKey(ref _allTeamsHeld, WireAction.AllTeams, down);

    private void SetKey(ref bool held, WireAction action, bool down)
    {
        if (held == down)
        {
            return;
        }
        held = down;
        if (_phase == LinkPhase.Online)
        {
            Send(new PressMessage(action, down, _seq++));
        }
        Publish();
    }

    /// <summary>Turns the open-mic latch on or off, while online. The bot ends it outside a team voice channel.</summary>
    public void SetOpenMic(bool on)
    {
        if (_phase != LinkPhase.Online || _openMic == on)
        {
            return;
        }
        _openMic = on;
        _notice = LinkNotice.None;
        if (on)
        {
            _openMicAskedAt = time.Now;
        }
        Send(new OpenMicMessage(on, _seq++));
        Publish();
    }

    /// <summary>Moves the clock: heartbeats, timeouts, reconnects, and the badge turning from Reconnecting to Disconnected.</summary>
    public void Tick()
    {
        var now = time.Now;
        switch (_phase)
        {
            case LinkPhase.Online:
                if (now - _lastReceivedAt >= _timeout)
                {
                    Drop();
                }
                else if (now >= _nextHeartbeatAt)
                {
                    SendHeartbeat();
                }
                break;
            case LinkPhase.Connecting:
                if (now - _attemptStartedAt >= _options.WelcomeDeadline)
                {
                    Drop();
                }
                break;
            case LinkPhase.SigningIn:
                if (now - _signInStartedAt >= _options.SignInLifetime)
                {
                    Fail(LinkFailure.SignInTimedOut);
                }
                break;
            case LinkPhase.Offline:
                if (_credential is LinkCredential.LoginId && now - _signInStartedAt >= _options.SignInLifetime)
                {
                    Fail(LinkFailure.SignInTimedOut);
                }
                else if (now >= _nextAttemptAt)
                {
                    BeginAttempt();
                }
                break;
        }
        Publish();
    }

    private void BeginAttempt()
    {
        var generation = ++_generation;
        _phase = _credential is LinkCredential.LoginId ? LinkPhase.SigningIn : LinkPhase.Connecting;
        _helloSent = false;
        _attemptStartedAt = time.Now;
        _connecting = new CancellationTokenSource();
        var transport = transports.Create();
        _transport = transport;
        transport.Received += data => OnReceived(generation, data);
        transport.Closed += code => OnClosed(generation, code);
        Publish();
        _ = ConnectAsync(generation, transport, _connecting.Token);
    }

    private async Task ConnectAsync(int generation, ILinkTransport transport, CancellationToken cancellation)
    {
        try
        {
            await transport.ConnectAsync(_options.ServerOverride ?? new Uri($"wss://{_host}/ws"), cancellation);
            if (generation != _generation)
            {
                return;
            }
            var hello = MessageCodec.Serialize(_credential!.ToHello(appVersion, installId));
            await transport.SendAsync(hello);
            if (generation == _generation)
            {
                _helloSent = true;
            }
        }
        catch (Exception) when (generation != _generation)
        {
            // Abandoned while connecting; whoever abandoned it already moved on.
        }
        catch (Exception)
        {
            Drop();
        }
    }

    private void OnReceived(int generation, ReadOnlyMemory<byte> data)
    {
        if (generation != _generation)
        {
            return;
        }
        _lastReceivedAt = time.Now;
        switch (MessageCodec.ParseServer(data.Span))
        {
            case MessageParse<ServerMessage>.Parsed parsed:
                Handle(parsed.Message);
                break;
            case MessageParse<ServerMessage>.UnsupportedVersion:
                Fail(LinkFailure.VersionMismatch);
                break;
            default:
                // A message we can't read from the bot: not worth guessing about; connect afresh.
                Drop();
                break;
        }
    }

    // The bot's close code for "replaced by a newer app". Reconnecting would only make two apps evict each other.
    private const int ReplacedCloseCode = 4000;

    private void OnClosed(int generation, int? closeCode)
    {
        if (generation != _generation)
        {
            return;
        }
        if (closeCode == ReplacedCloseCode)
        {
            Fail(LinkFailure.ReplacedByNewerApp);
            return;
        }
        Drop();
    }

    private void Handle(ServerMessage message)
    {
        switch (message)
        {
            case WelcomeMessage welcome when _phase is LinkPhase.Connecting or LinkPhase.SigningIn:
                OnWelcome(welcome);
                break;
            case StatusMessage status when _phase == LinkPhase.Online:
                _status = status;
                if (!status.OpenMic && _openMic && time.Now - _openMicAskedAt >= _options.OpenMicGrace)
                {
                    // The bot ended it (the player left their team's channels): don't re-assert it with the next heartbeat.
                    _openMic = false;
                }
                Publish();
                break;
            case HeartbeatAckMessage ack when _phase == LinkPhase.Online:
                var round = (long)time.Now.TotalMilliseconds - ack.SentAtMs;
                _roundTripMs = round is >= 0 and < int.MaxValue ? (int)round : _roundTripMs;
                Publish();
                break;
            case ErrorMessage error:
                OnError(error);
                break;
            default:
                // A welcome when already welcomed, or a status before it: the bot broke the protocol.
                Fail(LinkFailure.ProtocolError);
                break;
        }
    }

    private void OnWelcome(WelcomeMessage welcome)
    {
        if (welcome.DeviceToken is { } issued)
        {
            _credential = new LinkCredential.DeviceToken(issued);
        }
        else if (_credential is not LinkCredential.DeviceToken)
        {
            // Linked, but no token to come back with: the next start couldn't sign in.
            Fail(LinkFailure.ProtocolError);
            return;
        }
        _phase = LinkPhase.Online;
        _failure = LinkFailure.None;
        _failures = 0;
        _offlineSince = null;
        _displayName = welcome.DisplayName;
        _heartbeat = TimeSpan.FromMilliseconds(welcome.HeartbeatMs);
        _timeout = TimeSpan.FromMilliseconds(welcome.TimeoutMs);
        _status = null;
        _roundTripMs = null;
        _lastReceivedAt = time.Now;
        // The first heartbeat restates the held keys, so a key held through a reconnect carries on.
        SendHeartbeat();
        if (welcome.DeviceToken is { } token)
        {
            DeviceTokenIssued?.Invoke(token);
        }
        Publish();
    }

    private void OnError(ErrorMessage error)
    {
        switch (error.Code)
        {
            case ErrorCode.BadCode:
                Fail(LinkFailure.BadCode);
                break;
            case ErrorCode.Used:
                Fail(LinkFailure.CodeUsed);
                break;
            case ErrorCode.Expired:
                Fail(_credential is LinkCredential.DeviceToken ? LinkFailure.TokenExpired : LinkFailure.CodeExpired);
                break;
            case ErrorCode.Revoked:
                Fail(LinkFailure.Revoked);
                break;
            case ErrorCode.NotMember:
                Fail(LinkFailure.NotMember);
                break;
            case ErrorCode.VersionMismatch:
                Fail(LinkFailure.VersionMismatch);
                break;
        }
    }

    private void SendHeartbeat()
    {
        _nextHeartbeatAt = time.Now + _heartbeat;
        List<WireAction> held = [];
        if (_groupHeld)
        {
            held.Add(WireAction.Group);
        }
        if (_allTeamsHeld)
        {
            held.Add(WireAction.AllTeams);
        }
        Send(new HeartbeatMessage(_heartbeatSeq++, held, _openMic, (long)time.Now.TotalMilliseconds));
    }

    private void Send(ClientMessage message)
    {
        var generation = _generation;
        var transport = _transport;
        if (transport is null)
        {
            return;
        }
        _ = SendAsync(generation, transport, MessageCodec.Serialize(message));
    }

    private async Task SendAsync(int generation, ILinkTransport transport, byte[] bytes)
    {
        try
        {
            await transport.SendAsync(bytes);
        }
        catch (Exception) when (generation == _generation)
        {
            Drop();
        }
        catch (Exception)
        {
            // Superseded; nothing to do.
        }
    }

    /// <summary>The connection is gone or unusable: keep the link, try again after a pause.</summary>
    private void Drop()
    {
        if (_phase is LinkPhase.NotLinked or LinkPhase.Failed)
        {
            return;
        }
        var wasOnline = _phase == LinkPhase.Online;
        Teardown();
        if (wasOnline)
        {
            _offlineSince = time.Now;
            if (_openMic)
            {
                // A mic that reopens by itself after an outage would surprise the player; they latch it again.
                _openMic = false;
                _notice = LinkNotice.OpenMicOffAfterDrop;
            }
            _status = null;
            _roundTripMs = null;
        }
        if (_credential is LinkCredential.Code && _helloSent)
        {
            // The code may have been used up before the answer was lost; it can't be tried again.
            Fail(LinkFailure.Interrupted);
            return;
        }
        _failures++;
        _phase = LinkPhase.Offline;
        _nextAttemptAt = time.Now + _options.Backoff[Math.Min(_failures, _options.Backoff.Count) - 1];
        Publish();
    }

    private void Fail(LinkFailure failure)
    {
        var token = _credential is LinkCredential.DeviceToken;
        Teardown();
        _phase = LinkPhase.Failed;
        _failure = failure;
        _offlineSince = time.Now;
        _status = null;
        _openMic = false;
        Publish();
        if (token && failure is LinkFailure.Revoked or LinkFailure.TokenExpired)
        {
            TokenRejected?.Invoke();
        }
    }

    // Invalidates callbacks of the current transport and lets it go.
    private void Teardown()
    {
        _generation++;
        _connecting?.Cancel();
        _connecting = null;
        var transport = _transport;
        _transport = null;
        if (transport is not null)
        {
            _ = transport.DisposeAsync().AsTask().ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    private void Publish()
    {
        var snapshot = BuildSnapshot();
        if (snapshot.Equals(_snapshot))
        {
            return;
        }
        _snapshot = snapshot;
        Changed?.Invoke(snapshot);
    }

    private ConnectionSnapshot BuildSnapshot()
    {
        var online = _phase == LinkPhase.Online;
        var active = _status?.Active ?? [];
        var broadcasting = online && (_groupHeld || _allTeamsHeld || active.Count > 0);
        var badge = _phase switch
        {
            LinkPhase.Online => broadcasting ? BadgeState.Broadcasting : BadgeState.Connected,
            LinkPhase.Connecting or LinkPhase.Offline when time.Now - (_offlineSince ?? time.Now) < _options.ReconnectingFor
                => BadgeState.Reconnecting,
            _ => BadgeState.Disconnected,
        };
        var failure = _phase switch
        {
            LinkPhase.Online => LinkFailure.None,
            LinkPhase.NotLinked => LinkFailure.NotLinked,
            LinkPhase.Failed => _failure,
            LinkPhase.SigningIn => LinkFailure.None,
            _ => badge == BadgeState.Disconnected ? LinkFailure.ServerUnreachable : LinkFailure.None,
        };
        return new ConnectionSnapshot
        {
            Phase = _phase,
            Failure = failure,
            Badge = badge,
            Notice = _notice,
            DisplayName = online ? _displayName : null,
            ChannelName = online ? _status?.ChannelName : null,
            Group = online ? _status?.Group : null,
            Targets = online ? _status?.Targets ?? [] : [],
            Warnings = online ? _status?.Warnings ?? [] : [],
            BotDownIn = online ? _status?.BotDownIn ?? [] : [],
            GroupHeld = _groupHeld,
            AllTeamsHeld = _allTeamsHeld,
            OpenMic = _openMic,
            OpenMicActive = online && active.Contains(WireAction.OpenMic),
            RoundTripMs = online ? _roundTripMs : null,
            OfflineSince = online || _phase == LinkPhase.NotLinked ? null : _offlineSince,
            NextAttemptAt = _phase == LinkPhase.Offline ? _nextAttemptAt : null,
        };
    }
}
