using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Kzg.Bot.Control;
using Kzg.Core.Abstractions;
using Kzg.Core.Audio;
using Kzg.Core.Config;
using Kzg.Core.Routing;
using Microsoft.Extensions.Logging;

namespace Kzg.Bot.Engine;

/// <summary>
/// The audio engine: one thread that, every 20 ms, drains what other threads queued, reads each
/// player's jitter buffer, decodes, asks <see cref="RoutingEngine"/> who is
/// heard where, reads each <see cref="IAudioSource"/>, mixes per destination VC and sends. Everything but the <c>Post</c>-style entry points
/// runs on that thread, so routing and buffers hold no locks. Library threads and the control server only
/// enqueue.
/// </summary>
public sealed class AudioEngine : IAppEvents, IDisposable
{
    private const int HousekeepingEvery = 50;

    private readonly IOpusCodecFactory _codecs;
    private readonly ITimeSource _time;
    private readonly EngineOptions _options;
    private readonly ILogger? _log;
    private readonly Action<TimeSpan> _sleep;
    private readonly IVoiceGateway _gateway;

    private readonly ConcurrentQueue<Action<TimeSpan>> _commands = new();
    private int _queuedPackets;

    // Everything below is the engine thread's alone.
    private readonly RoutingEngine _routing;
    private readonly Dictionary<ulong, UserAudio> _users = [];
    private readonly Dictionary<ChannelId, LinkState> _links = [];
    private volatile IReadOnlySet<ChannelId> _readyChannels = new HashSet<ChannelId>();
    private readonly Dictionary<ulong, MixInput> _frames = [];
    private readonly List<MixInput> _inputs = [];
    private readonly List<(IVoiceChannelLink Link, Action<OpusPacket> Handler)> _subscriptions = [];
    private readonly SourceAudio[] _sources;
    private readonly HashSet<ChannelId> _mixed = [];
    private long _tickCount;
    private int _playerBuffers;

    private Thread? _thread;
    private CancellationTokenSource? _stop;

    private long _ticks, _framesSent, _packetsDropped, _decodeErrors, _sendErrors, _tickFaults, _resyncs, _concealed, _sourceErrors;

    /// <param name="sleep">Waits this long on the engine thread; tests replace it to run the clock by hand.</param>
    /// <param name="sources">Audio that is not a player's, such as jungle calls, read once a tick on the engine thread.</param>
    public AudioEngine(
        BotConfig config,
        IVoiceGateway gateway,
        IOpusCodecFactory codecs,
        ITimeSource time,
        EngineOptions? options = null,
        ILogger? log = null,
        Action<TimeSpan>? sleep = null,
        IReadOnlyList<IAudioSource>? sources = null)
    {
        _sources = [.. (sources ?? []).Select(source => new SourceAudio(source))];
        (_gateway, _codecs, _time) = (gateway, codecs, time);
        (_options, _log) = (options ?? new EngineOptions(), log);
        _sleep = sleep ?? (wait => Thread.Sleep(Math.Max(1, (int)Math.Ceiling(Math.Min(wait.TotalMilliseconds, OpusFormat.FrameMs)))));
        _routing = new RoutingEngine(RoutingConfig.From(config));
        // Subscribed before Links is read, so a link added in between is seen by one or both and kept once.
        gateway.LinkAdded += OnLinkAdded;
        gateway.LinkRemoved += OnLinkRemoved;
        foreach (var link in gateway.Links)
        {
            Attach(link);
        }
        gateway.PresenceChanged += OnPresence;
    }

    /// <summary>A routing decision changed what an app should show; raised on the engine thread.</summary>
    public event Action<UserStatus>? StatusChanged;

    /// <summary>The set of VCs whose relay link is ready changed; raised on the engine thread, after routing was told.</summary>
    public event Action? LinksChanged;

    /// <summary>The VCs whose relay link is ready now. A fresh set each time it changes, so a reader never sees it half built.</summary>
    public IReadOnlySet<ChannelId> ReadyChannels => _readyChannels;

    /// <summary>How many players the engine holds a buffer for; players gone quiet are dropped, so this stays near who is talking.</summary>
    public int PlayerBuffers => Volatile.Read(ref _playerBuffers);

    public EngineStats Stats => new(
        Volatile.Read(ref _ticks), Volatile.Read(ref _framesSent), Volatile.Read(ref _packetsDropped),
        Volatile.Read(ref _decodeErrors), Volatile.Read(ref _sendErrors), Volatile.Read(ref _tickFaults), Volatile.Read(ref _resyncs),
        Volatile.Read(ref _concealed), Volatile.Read(ref _sourceErrors));

    /// <summary>
    /// Told after each tick, on the engine thread: how late it started against its slot, and how long it took.
    /// For load measurement; null costs nothing. It must return quickly.
    /// </summary>
    public Action<TimeSpan, TimeSpan>? TickObserver { get; set; }

    // ---- Entry points for other threads: they only enqueue.

    /// <summary>A new config, such as after <c>/gvg bind</c>; it applies from the next tick.</summary>
    public void Configure(BotConfig config) => Post(_ => _routing.Configure(RoutingConfig.From(config)));

    /// <summary>A press, an open-mic toggle or a synthetic edge: a claim routing checks.</summary>
    public void OnEdge(BroadcastEdge edge) => Post(_ => _routing.OnEdge(edge));

    public void Connected(UserId user, TimeSpan at) => Post(_ => _routing.OnAppConnected(user, at));

    public void Disconnected(UserId user) => Post(_ => _routing.OnAppDisconnected(user));

    public void Heartbeat(UserId user, IReadOnlyList<BroadcastAction> held, bool openMic, TimeSpan at)
    {
        var copy = held.ToArray();
        Post(_ => _routing.OnHeartbeat(user, copy, openMic, at));
    }

    // ---- The thread.

    /// <summary>Starts the engine thread, which ticks every 20 ms until <see cref="Stop"/>.</summary>
    public void Start()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("the engine is already running");
        }
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _thread = new Thread(() => Run(token)) { IsBackground = true, Name = "Kzg audio engine", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Stops the engine thread and waits for it.</summary>
    public void Stop()
    {
        _stop?.Cancel();
        _thread?.Join();
        (_thread, _stop) = (null, null);
    }

    /// <summary>
    /// Ticks on this thread until cancelled, paced as Gengfu's <c>VoiceSender</c> is: tick <c>i</c> runs at
    /// <c>start + 20 ms * i - Lead</c> by the injected clock, catching up after a short stall. After a
    /// long one it takes its slots from now rather than burst.
    /// </summary>
    public void Run(CancellationToken cancellationToken)
    {
        var start = _time.Now + _options.Lead;
        for (long i = 0; !cancellationToken.IsCancellationRequested; i++)
        {
            var due = start + (EngineOptions.FrameLength * i) - _options.Lead;
            for (var wait = due - _time.Now; wait > TimeSpan.Zero && !cancellationToken.IsCancellationRequested; wait = due - _time.Now)
            {
                _sleep(wait);
            }
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            var now = _time.Now;
            if (now - due > _options.MaxBehind)
            {
                start += now - due;
                Interlocked.Increment(ref _resyncs);
            }
            try
            {
                Tick(now);
            }
            catch (Exception exception)
            {
                // The thread outlives a bad tick, or every call after it would be lost.
                Interlocked.Increment(ref _tickFaults);
                _log?.LogError(exception, "engine tick failed");
            }
            TickObserver?.Invoke(now - due, _time.Now - now);
        }
    }

    public void Dispose()
    {
        Stop();
        _gateway.LinkAdded -= OnLinkAdded;
        _gateway.LinkRemoved -= OnLinkRemoved;
        foreach (var (link, handler) in _subscriptions)
        {
            link.PacketReceived -= handler;
        }
        _gateway.PresenceChanged -= OnPresence;
    }

    // ---- One tick, on the engine thread (or a test's, driving it by hand).

    public void Tick() => Tick(_time.Now);

    public void Tick(TimeSpan now)
    {
        Interlocked.Increment(ref _ticks);
        Drain(now);
        PollLinks();
        var plan = _routing.Tick(now);
        Relay(plan, now);
        Report(plan);
        if (++_tickCount % HousekeepingEvery == 0)
        {
            Housekeep(now);
        }
    }

    private void Drain(TimeSpan now)
    {
        while (_commands.TryDequeue(out var command))
        {
            command(now);
        }
    }

    /// <summary>A link has no ready event, so the engine polls it and tells routing when it changes.</summary>
    private void PollLinks()
    {
        var changed = false;
        foreach (var state in _links.Values)
        {
            var ready = state.Link.Ready;
            if (ready != state.Ready)
            {
                state.Ready = ready;
                _routing.OnLinkReady(state.Link.Channel, ready);
                changed = true;
            }
        }
        if (changed)
        {
            PublishReadyChannels();
        }
    }

    private void PublishReadyChannels()
    {
        _readyChannels = _links.Values.Where(state => state.Ready).Select(state => state.Link.Channel).ToHashSet();
        try
        {
            LinksChanged?.Invoke();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _log?.LogWarning("a link handler failed: {Error}", exception.GetType().Name);
        }
    }

    private void Relay(TickPlan plan, TimeSpan now)
    {
        foreach (var ended in plan.Ended)
        {
            if (_users.TryGetValue(ended.V, out var user))
            {
                user.Buffer.Stop();
                user.RelayDecoder = null;
            }
        }
        // Each relaying player is read and decoded once, however many VCs they reach.
        _frames.Clear();
        foreach (var route in plan.Routes)
        {
            var user = AudioOf(route.User, now);
            if (route.ReadFrom is not null || !user.Buffer.Relaying)
            {
                user.Buffer.Start(route.ReadFrom ?? user.RestartFrom ?? now);
                user.RestartFrom = null;
                // A fresh decoder: the last relay's state would colour the first frame of this one.
                user.RelayDecoder = _codecs.Decoder();
            }
            // A drain after a key-up plays what arrived up to the release, then ends.
            if (route.DrainUntil is { } until && user.Buffer.RelayPassed(until))
            {
                _routing.OnRelayDrained(route.User);
                _frames[route.User.V] = new MixInput(FrameRead.Silent, user.RelayPcm);
                continue;
            }
            var frame = user.Buffer.Read(now);
            if (frame.Kind == FrameKind.Missing)
            {
                // The relay needed a frame the buffer didn't have: an underrun, concealed by the decoder.
                Interlocked.Increment(ref _concealed);
            }
            if (frame.Kind != FrameKind.Silent)
            {
                try
                {
                    FrameDecoding.Decode(user.RelayDecoder!, frame, user.RelayPcm);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // A packet that won't decode is not passed on to the team either.
                    Interlocked.Increment(ref _decodeErrors);
                    _log?.LogWarning("a relayed frame didn't decode: {Error}", exception.GetType().Name);
                    frame = FrameRead.Silent;
                }
            }
            _frames[route.User.V] = new MixInput(frame, user.RelayPcm);
        }
        foreach (var source in _sources)
        {
            ReadSource(source);
        }
        foreach (var state in _links.Values)
        {
            state.SentThisTick = false;
        }
        // Each VC is mixed once a tick: the players' destinations, then the VCs only a source reaches.
        _mixed.Clear();
        foreach (var destination in plan.Destinations)
        {
            MixOnce(plan, destination);
        }
        foreach (var source in _sources)
        {
            foreach (var destination in source.Targets)
            {
                MixOnce(plan, destination);
            }
        }
        foreach (var state in _links.Values)
        {
            UpdateSpeaking(state);
        }
    }

    /// <summary>
    /// A source's frame for this tick, decoded, and the VCs it reaches now. A source that throws or gives a
    /// frame that won't decode is silent this tick, so the players' relay goes on.
    /// </summary>
    private void ReadSource(SourceAudio source)
    {
        var frame = FrameRead.Silent;
        IReadOnlyList<ChannelId> targets = [];
        try
        {
            frame = source.Source.Read(_tickCount);
            if (frame.Kind != FrameKind.Silent)
            {
                targets = _routing.Resolve(source.Source.Targets);
            }
            source.Failing = false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Interlocked.Increment(ref _sourceErrors);
            // Once per run of failures: a source broken for good would otherwise log 50 lines a second.
            if (!source.Failing)
            {
                _log?.LogWarning("an audio source failed: {Source} {Error}", source.Source.Id.V, exception.GetType().Name);
            }
            (source.Failing, frame) = (true, FrameRead.Silent);
        }
        if (frame.Kind == FrameKind.Silent)
        {
            // The next clip starts on a fresh decoder, uncoloured by the last.
            (source.Decoder, source.Input, source.Targets) = (null, default, []);
            return;
        }
        source.Decoder ??= _codecs.Decoder();
        try
        {
            FrameDecoding.Decode(source.Decoder, frame, source.Pcm);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Interlocked.Increment(ref _decodeErrors);
            _log?.LogWarning("a source frame didn't decode: {Source} {Error}", source.Source.Id.V, exception.GetType().Name);
            (source.Input, source.Targets) = (default, []);
            return;
        }
        (source.Input, source.Targets) = (new MixInput(frame, source.Pcm), targets);
    }

    private void MixOnce(TickPlan plan, ChannelId destination)
    {
        if (_mixed.Add(destination) && _links.TryGetValue(destination, out var state))
        {
            Mix(plan, destination, state);
        }
    }

    private void Mix(TickPlan plan, ChannelId destination, LinkState state)
    {
        _inputs.Clear();
        foreach (var user in plan.SourcesFor(destination))
        {
            _inputs.Add(_frames[user.V]);
        }
        foreach (var source in _sources)
        {
            if (source.Reaches(destination))
            {
                _inputs.Add(source.Input);
            }
        }
        try
        {
            var output = state.Mixer.Mix(CollectionsMarshal.AsSpan(_inputs));
            if (output.Kind == MixKind.Nothing)
            {
                return;
            }
            state.Link.Send(output.Opus);
            state.SentThisTick = true;
            Interlocked.Increment(ref _framesSent);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // One VC's bot failing must not stop the others hearing the call.
            Interlocked.Increment(ref _sendErrors);
            _log?.LogWarning("sending to a VC failed: {Error}", exception.GetType().Name);
        }
    }

    private void UpdateSpeaking(LinkState state)
    {
        if (state.SentThisTick)
        {
            state.QuietTicks = 0;
            if (!state.Speaking)
            {
                SetSpeaking(state, true);
            }
        }
        else if (state.Speaking && ++state.QuietTicks >= _options.SpeakingHangoverTicks)
        {
            SetSpeaking(state, false);
        }
    }

    private void SetSpeaking(LinkState state, bool on)
    {
        state.Speaking = on;
        try
        {
            state.Link.SetSpeaking(on);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Interlocked.Increment(ref _sendErrors);
            _log?.LogWarning("setting a VC's speaking flag failed: {Error}", exception.GetType().Name);
        }
    }

    private void Report(TickPlan plan)
    {
        foreach (var status in plan.StatusChanges)
        {
            try
            {
                StatusChanged?.Invoke(status);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _log?.LogWarning("a status handler failed: {Error}", exception.GetType().Name);
            }
        }
    }

    /// <summary>Drops the buffers of players who went quiet long ago, so a long night doesn't hold every voice it ever heard.</summary>
    private void Housekeep(TimeSpan now)
    {
        List<ulong> idle = [];
        foreach (var (id, user) in _users)
        {
            if (!user.Buffer.Relaying && now - user.LastPacket > _options.IdleUserAfter)
            {
                idle.Add(id);
            }
        }
        foreach (var id in idle)
        {
            _users.Remove(id);
        }
        Volatile.Write(ref _playerBuffers, _users.Count);
    }

    // ---- Queued work.

    /// <summary>Starts using a link; one already in use for its channel is dropped for it. The engine thread, or the constructor.</summary>
    private void Attach(IVoiceChannelLink link)
    {
        if (_links.TryGetValue(link.Channel, out var existing))
        {
            if (ReferenceEquals(existing.Link, link))
            {
                return;
            }
            Detach(link.Channel);
        }
        _links[link.Channel] = new LinkState(link, Mixer.Create(_codecs));
        var channel = link.Channel;
        Action<OpusPacket> handler = packet => PostPacket(packet, channel);
        link.PacketReceived += handler;
        _subscriptions.Add((link, handler));
    }

    /// <summary>Stops using the link of a channel and tells routing its bot is gone. The engine thread.</summary>
    private void Detach(ChannelId channel)
    {
        if (!_links.Remove(channel, out var state))
        {
            return;
        }
        var index = _subscriptions.FindIndex(pair => ReferenceEquals(pair.Link, state.Link));
        if (index >= 0)
        {
            state.Link.PacketReceived -= _subscriptions[index].Handler;
            _subscriptions.RemoveAt(index);
        }
        if (state.Speaking)
        {
            SetSpeaking(state, false);
        }
        if (state.Ready)
        {
            _routing.OnLinkReady(channel, false);
        }
        PublishReadyChannels();
    }

    private void OnLinkAdded(IVoiceChannelLink link) => Post(_ => Attach(link));

    private void OnLinkRemoved(IVoiceChannelLink link) => Post(_ =>
    {
        // Only the link that was removed: a replacement for the same channel may already be in.
        if (_links.TryGetValue(link.Channel, out var state) && ReferenceEquals(state.Link, link))
        {
            Detach(link.Channel);
        }
    });

    private void OnPresence(UserId user, ChannelId? channel) => Post(_ => _routing.OnPresence(user, channel));

    private void PostPacket(OpusPacket packet, ChannelId channel)
    {
        if (Interlocked.Increment(ref _queuedPackets) > _options.MaxQueuedPackets)
        {
            Interlocked.Decrement(ref _queuedPackets);
            Interlocked.Increment(ref _packetsDropped);
            return;
        }
        // Copied now: the voice library may reuse its buffer as soon as this returns.
        var copy = packet with { Data = packet.Data.ToArray() };
        _commands.Enqueue(now =>
        {
            Interlocked.Decrement(ref _queuedPackets);
            var user = AudioOf(copy.User, now);
            // A player who moved to another VC, or came back to this one, sends a stream of new numbers.
            if ((user.Origin is { } origin && origin != channel) || (copy.Ssrc != 0 && user.Ssrc != 0 && user.Ssrc != copy.Ssrc))
            {
                user.StartNewStream(copy.Arrival);
            }
            else if (user.HasRtp && NewTalkSpurt(user, copy))
            {
                user.StartNewStream(copy.Arrival);
            }
            user.Origin = channel;
            if (copy.Ssrc != 0)
            {
                user.Ssrc = copy.Ssrc;
            }
            user.Buffer.Add(copy);
            user.LastPacket = copy.Arrival;
            (user.LastRtpTs, user.HasRtp) = (copy.RtpTs, true);
        });
    }

    /// <summary>
    /// Whether a packet follows its player's last by far more time than its timestamp accounts for: a client
    /// that numbers the next packet as the last one's successor after a pause. Its timestamps then say
    /// "straight after", while the clock says seconds later; the buffer is started afresh on it.
    /// </summary>
    private bool NewTalkSpurt(UserAudio user, OpusPacket packet)
    {
        var arrivalGap = packet.Arrival - user.LastPacket;
        var timestampGap = TimeSpan.FromSeconds((int)(packet.RtpTs - user.LastRtpTs) / (double)OpusFormat.SampleRate);
        return arrivalGap - timestampGap > _options.TalkSpurtGap;
    }

    private void Post(Action<TimeSpan> command) => _commands.Enqueue(command);

    private UserAudio AudioOf(UserId user, TimeSpan now)
    {
        if (!_users.TryGetValue(user.V, out var audio))
        {
            _users[user.V] = audio = new UserAudio(new JitterBuffer(), now);
            Volatile.Write(ref _playerBuffers, _users.Count);
        }
        return audio;
    }

}

/// <summary>One player's audio state: their buffer, the relay decoder and its scratch.</summary>
internal sealed class UserAudio(JitterBuffer buffer, TimeSpan created)
{
    public JitterBuffer Buffer { get; } = buffer;

    /// <summary>The RTP timestamp of the last packet, once there has been one.</summary>
    public uint LastRtpTs { get; set; }

    public bool HasRtp { get; set; }

    /// <summary>The stream the last packet belonged to; 0 until a packet says.</summary>
    public uint Ssrc { get; set; }

    /// <summary>Where a relay under way restarts on a new buffer; null when nothing is pending.</summary>
    public TimeSpan? RestartFrom { get; set; }

    /// <summary>The VC the last packet came from.</summary>
    public ChannelId? Origin { get; set; }

    /// <summary>
    /// The player's packets now come from a new stream, which starts at numbers of its own. The old buffer
    /// would drop them as late, and the old decoders would colour the first frames, so both start afresh.
    /// The buffer keeps its playout delay: the new stream comes over the same network.
    /// </summary>
    public void StartNewStream(TimeSpan arrival)
    {
        Buffer.Restart();
        // A relay under way carries on from the new stream's first packet, not from the tick that notices.
        RestartFrom = arrival;
        RelayDecoder = null;
    }

    public TimeSpan LastPacket { get; set; } = created;

    /// <summary>
    /// The relay read head's decoder. The relay reads frames at another position than the live head
    /// does, so it can't share a decoder: Opus state follows the stream, in order.
    /// </summary>
    public IOpusDecoder? RelayDecoder { get; set; }

    public short[] RelayPcm { get; } = new short[OpusFormat.FrameShorts];
}

/// <summary>One <see cref="IAudioSource"/>'s state on the engine thread: its decoder, this tick's frame and where it goes.</summary>
internal sealed class SourceAudio(IAudioSource source)
{
    public IAudioSource Source { get; } = source;

    /// <summary>Null while the source is silent, so each clip starts on a fresh one.</summary>
    public IOpusDecoder? Decoder { get; set; }

    public short[] Pcm { get; } = new short[OpusFormat.FrameShorts];

    /// <summary>This tick's frame and its PCM; silent when the source gave nothing.</summary>
    public MixInput Input { get; set; }

    /// <summary>The VCs this tick's frame reaches; empty when silent.</summary>
    public IReadOnlyList<ChannelId> Targets { get; set; } = [];

    /// <summary>The last read threw, so a failure that goes on is logged once.</summary>
    public bool Failing { get; set; }

    public bool Reaches(ChannelId destination)
    {
        foreach (var target in Targets)
        {
            if (target == destination)
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>One relay bot's link with its destination mixer and what the engine knows of its state.</summary>
internal sealed class LinkState(IVoiceChannelLink link, Mixer mixer)
{
    public IVoiceChannelLink Link { get; } = link;

    public Mixer Mixer { get; } = mixer;

    /// <summary>What routing was last told; a link starts as not ready until the first poll says otherwise.</summary>
    public bool Ready { get; set; }

    public bool Speaking { get; set; }

    public bool SentThisTick { get; set; }

    public int QuietTicks { get; set; }
}
