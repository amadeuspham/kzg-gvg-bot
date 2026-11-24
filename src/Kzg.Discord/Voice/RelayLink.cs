using Kzg.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Kzg.Discord.Voice;

/// <summary>
/// One relay bot in one voice channel, as the engine sees it. It outlives the voice connection under it:
/// when the connection drops the link is simply not ready, and a new one is bound when the bot rejoins.
/// </summary>
public sealed class RelayLink : IVoiceChannelLink, IDisposable
{
    /// <summary>The silent Opus frame that tells a receiver a burst has ended; Discord clients send it too.</summary>
    public static readonly byte[] SilenceFrame = [0xF8, 0xFF, 0xFE];

    /// <summary>How many of them end a burst.</summary>
    public const int SilenceFrames = 5;

    /// <summary>
    /// How long a burst's first frames wait, after the speaking flag is out, before they go. Discord tells a
    /// receiver whose stream is whose from that flag, and a receiver drops the packets of a stream it can't
    /// place yet: a player who joined after the bot never saw the flag go up, so the first frames of the
    /// bot's first burst reach them undecryptable. The flag travels by the gateway socket and the audio by
    /// UDP, and the wait gives the flag the lead. It applies only to a burst that someone new may be hearing
    /// for the first time: the first after a connection comes up and the first after a player joins.
    /// </summary>
    public static readonly TimeSpan FlagLead = TimeSpan.FromMilliseconds(80);

    /// <summary>
    /// How long a bot waits after a player joins before it raises the flag for them, with no audio behind it:
    /// a player joining a channel hears nothing from the bot until the flag has gone up while they are there.
    /// The wait lets one join settle, and several in a row share a single announcement.
    /// </summary>
    public static readonly TimeSpan JoinSettle = TimeSpan.FromMilliseconds(1500);

    /// <summary>How long an announcement keeps the flag up.</summary>
    public static readonly TimeSpan Announcement = TimeSpan.FromMilliseconds(250);

    private readonly ITimeSource _time;
    private readonly OwnBots _ownBots;
    private readonly Func<ulong> _botUserId;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger? _log;
    private readonly Lock _gate = new();
    private readonly RtpClock _rtp = new();
    private readonly CancellationTokenSource _disposed = new();

    private IVoiceConnection? _connection;
    private volatile bool _up;
    private bool _speaking;
    private bool _bursting;
    private bool _newListeners = true;
    private int _announce;
    private List<byte[]>? _held;
    private int _burst;
    private long _sent, _sendErrors, _droppedOwn, _unknownSsrc, _sentNotReady;

    internal RelayLink(
        ChannelId channel,
        Func<ulong> botUserId,
        ITimeSource time,
        OwnBots ownBots,
        ILogger? log = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        (Channel, _botUserId, _time, _ownBots, _log) = (channel, botUserId, time, ownBots, log);
        _delay = delay ?? Task.Delay;
    }

    public ulong BotUserId => _botUserId();

    public ChannelId Channel { get; }

    /// <summary>The bot is in the channel with its media path up. A cheap read: the engine asks every tick.</summary>
    public bool Ready => _up;

    public event Action<OpusPacket> PacketReceived = delegate { };

    public long FramesSent => Interlocked.Read(ref _sent);

    public long SendErrors => Interlocked.Read(ref _sendErrors);

    public long DroppedOwn => Interlocked.Read(ref _droppedOwn);

    public long UnknownSsrc => Interlocked.Read(ref _unknownSsrc);

    public long SentNotReady => Interlocked.Read(ref _sentNotReady);

    /// <summary>True while the burst's first frames wait for the flag's lead. Tests wait on it instead of sleeping.</summary>
    internal bool HoldingFrames
    {
        get
        {
            lock (_gate)
            {
                return _held is not null;
            }
        }
    }

    /// <summary>Puts a fresh connection under the link; it becomes ready when the connection says so.</summary>
    internal void Bind(IVoiceConnection connection)
    {
        Unbind();
        lock (_gate)
        {
            _connection = connection;
            (_speaking, _bursting, _held, _newListeners) = (false, false, null, true);
        }
        connection.Frame += OnFrame;
        connection.UserJoined += OnUserJoined;
        connection.Ready += OnUp;
        connection.Resumed += OnUp;
        connection.Interrupted += OnDown;
        // After subscribing, so a Ready that came during the join is not missed and a drop since is not hidden.
        _up = connection.IsReady;
        if (_up)
        {
            Announce(TimeSpan.Zero);
        }
    }

    /// <summary>Takes the connection away: the link is not ready until another is bound.</summary>
    internal void Unbind()
    {
        IVoiceConnection? old;
        lock (_gate)
        {
            old = _connection;
            _connection = null;
            _up = false;
            _held = null;
            _announce++;
            _burst++;
        }
        if (old is not null)
        {
            old.Frame -= OnFrame;
            old.UserJoined -= OnUserJoined;
            old.Ready -= OnUp;
            old.Resumed -= OnUp;
            old.Interrupted -= OnDown;
        }
    }

    public void Send(ReadOnlyMemory<byte> opus)
    {
        if (opus.IsEmpty)
        {
            return;
        }
        IVoiceConnection? begin;
        List<byte[]>? held;
        lock (_gate)
        {
            if (!_up || _connection is not { } connection)
            {
                _sentNotReady++;
                return;
            }
            _burst++;
            _bursting = true;
            if (_held is not null)
            {
                _held.Add(opus.ToArray());
                return;
            }
            if (_speaking)
            {
                SendLocked(connection, opus.Span);
                return;
            }
            _speaking = true;
            if (!_newListeners)
            {
                // Everyone in the channel has seen the flag go up: it can follow the frame.
                SendLocked(connection, opus.Span);
                begin = null;
                held = null;
            }
            else
            {
                _newListeners = false;
                held = _held = [opus.ToArray()];
                begin = connection;
            }
        }
        if (begin is null)
        {
            Observe(SetSpeakingAsync(true));
            return;
        }
        _ = BeginBurstAsync(begin, held!);
    }

    /// <summary>
    /// On: the speaking flag goes up. Off: five silent frames end the burst, 20 ms apart, then the flag goes
    /// down. New audio in the meantime cancels the ending.
    /// </summary>
    public void SetSpeaking(bool on)
    {
        if (on)
        {
            bool enter;
            lock (_gate)
            {
                _burst++;
                enter = !_speaking && _connection is not null;
                _speaking = _speaking || enter;
            }
            if (enter)
            {
                Observe(SetSpeakingAsync(true));
            }
            return;
        }
        int burst;
        lock (_gate)
        {
            if (!_bursting)
            {
                return;
            }
            burst = ++_burst;
        }
        _ = EndBurstAsync(burst);
    }

    public void Dispose()
    {
        _disposed.Cancel();
        Unbind();
        _disposed.Dispose();
    }

    private async Task EndBurstAsync(int burst)
    {
        try
        {
            for (var i = 0; i < SilenceFrames; i++)
            {
                lock (_gate)
                {
                    if (_burst != burst || !_up || _connection is not { } connection)
                    {
                        return;
                    }
                    EmitLocked(connection, SilenceFrame);
                }
                await _delay(TimeSpan.FromMilliseconds(OpusFormat.FrameMs), _disposed.Token).ConfigureAwait(false);
            }
            IVoiceConnection? ending;
            lock (_gate)
            {
                // Audio still waiting for the flag to land keeps it up; the burst that follows ends it.
                if (_burst != burst || _held is not null || _connection is not { } connection)
                {
                    return;
                }
                (_speaking, _bursting) = (false, false);
                ending = connection;
            }
            await ending.SetSpeakingAsync(false).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The link is going away.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _log?.LogDebug("ending a burst failed: {Error}", exception.GetType().Name);
        }
    }

    // Called under the gate: behind frames still waiting for the flag, so the order on the wire is the order given.
    private void EmitLocked(IVoiceConnection connection, byte[] frame)
    {
        if (_held is not null)
        {
            _held.Add(frame);
            return;
        }
        SendLocked(connection, frame);
    }

    // Called under the gate: one writer at a time keeps sequence numbers in the order they hit the wire.
    private void SendLocked(IVoiceConnection connection, ReadOnlySpan<byte> opus)
    {
        var (sequence, timestamp) = _rtp.Next(_time.Now);
        try
        {
            connection.Send(sequence, timestamp, opus);
            _sent++;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A dropped frame is a click; a thrown one every 20 ms would flood the engine's log.
            if (++_sendErrors is 1 or 100 or 1000)
            {
                _log?.LogWarning("a relay link couldn't send ({Count} so far): {Error}", _sendErrors, exception.GetType().Name);
            }
        }
    }

    private Task SetSpeakingAsync(bool on)
    {
        IVoiceConnection? connection;
        lock (_gate)
        {
            connection = _connection;
        }
        return connection is null ? Task.CompletedTask : connection.SetSpeakingAsync(on);
    }

    private void Observe(Task task) =>
        task.ContinueWith(
            t => _log?.LogDebug("setting the speaking flag failed: {Error}", t.Exception?.GetBaseException().GetType().Name),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private void OnUp()
    {
        lock (_gate)
        {
            // A connection that came back may have lost the flag: the next burst raises it again, with a lead.
            _speaking = _held is not null;
            _newListeners = true;
            _up = true;
        }
        Announce(TimeSpan.Zero);
    }

    /// <summary>Raises the flag after <paramref name="after"/>, with no audio behind it, unless a burst begins first.</summary>
    private void Announce(TimeSpan after)
    {
        IVoiceConnection connection;
        int announce;
        lock (_gate)
        {
            if (_connection is not { } current)
            {
                return;
            }
            connection = current;
            announce = ++_announce;
        }
        _ = AnnounceAsync(connection, announce, after);
    }

    private async Task AnnounceAsync(IVoiceConnection connection, int announce, TimeSpan after)
    {
        var burst = 0;
        try
        {
            if (after > TimeSpan.Zero)
            {
                await _delay(after, _disposed.Token).ConfigureAwait(false);
            }
            lock (_gate)
            {
                // A later join, a burst or a new connection has taken over.
                if (_announce != announce || !_up || _speaking || !ReferenceEquals(_connection, connection))
                {
                    return;
                }
                burst = ++_burst;
                _speaking = true;
            }
            await connection.SetSpeakingAsync(true).ConfigureAwait(false);
            await _delay(Announcement, _disposed.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_burst != burst || !ReferenceEquals(_connection, connection))
                {
                    return;
                }
                // A player who joined during the announcement has not heard it: their own follows.
                _speaking = false;
                _newListeners = _announce != announce;
            }
            await connection.SetSpeakingAsync(false).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The link is going away.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _log?.LogDebug("announcing a stream failed: {Error}", exception.GetType().Name);
            lock (_gate)
            {
                // Nothing went up, so nothing is left to bring down.
                if (burst != 0 && _burst == burst)
                {
                    _speaking = false;
                }
            }
        }
    }

    /// <summary>Raises the flag, waits for it to lead, then sends what the burst has queued meanwhile.</summary>
    private async Task BeginBurstAsync(IVoiceConnection connection, List<byte[]> held)
    {
        try
        {
            await connection.SetSpeakingAsync(true).ConfigureAwait(false);
            if (FlagLead > TimeSpan.Zero)
            {
                await _delay(FlagLead, _disposed.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The link is going away.
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Audio without the flag is still better than none.
            _log?.LogDebug("raising the speaking flag failed: {Error}", exception.GetType().Name);
        }
        lock (_gate)
        {
            if (!ReferenceEquals(_held, held))
            {
                return;
            }
            _held = null;
            if (!_up || !ReferenceEquals(_connection, connection))
            {
                // The connection dropped while the flag was in flight: the burst is lost, the next starts clean.
                _speaking = false;
                return;
            }
            foreach (var frame in held)
            {
                SendLocked(connection, frame);
            }
        }
    }

    private void OnDown() => _up = false;

    private void OnUserJoined()
    {
        lock (_gate)
        {
            _newListeners = true;
        }
        Announce(JoinSettle);
    }

    /// <summary>On the library's receive thread: the buffer is its own and is reused when this returns, so copy it.</summary>
    private void OnFrame(uint ssrc, ushort sequence, uint timestamp, ReadOnlySpan<byte> frame)
    {
        if (frame.IsEmpty)
        {
            return;
        }
        IVoiceConnection? connection;
        lock (_gate)
        {
            connection = _connection;
        }
        if (connection is null || !connection.TryGetUser(ssrc, out var user))
        {
            Interlocked.Increment(ref _unknownSsrc);
            return;
        }
        if (_ownBots.Contains(user))
        {
            Interlocked.Increment(ref _droppedOwn);
            return;
        }
        PacketReceived(new OpusPacket(new UserId(user), sequence, timestamp, frame.ToArray(), _time.Now, ssrc));
    }
}
