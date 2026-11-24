using Kzg.Core.Abstractions;

namespace Kzg.Core.Testing;

/// <summary>One frame a link was sent, and when.</summary>
public sealed record SentFrame(TimeSpan At, byte[] Opus);

/// <summary>A speaking flag a link was told to set, and when.</summary>
public sealed record SpeakingChange(TimeSpan At, bool On);

/// <summary>
/// A relay bot's link with no Discord behind it. Tests feed it users' Opus frames as packets, with the
/// sequence numbers and timestamps a real sender writes, and read back what the engine sent and when.
/// Thread-safe: the engine's thread writes while a test's reads.
/// </summary>
public sealed class FakeVoiceChannelLink(ChannelId channel, ulong botUserId, ITimeSource time) : IVoiceChannelLink
{
    private readonly Lock _gate = new();
    private readonly List<SentFrame> _sent = [];
    private readonly List<SpeakingChange> _speaking = [];
    private readonly Dictionary<ulong, Sender> _senders = [];
    private volatile bool _ready = true;
    private int _sentWhileNotReady;

    public ulong BotUserId { get; } = botUserId;

    public ChannelId Channel { get; } = channel;

    /// <summary>Whether the link can send; a test sets it to bring the bot down and up.</summary>
    public bool Ready
    {
        get => _ready;
        set => _ready = value;
    }

    public event Action<OpusPacket> PacketReceived = delegate { };

    /// <summary>Every frame sent so far, oldest first.</summary>
    public IReadOnlyList<SentFrame> Sent
    {
        get
        {
            lock (_gate)
            {
                return [.. _sent];
            }
        }
    }

    public IReadOnlyList<SpeakingChange> SpeakingChanges
    {
        get
        {
            lock (_gate)
            {
                return [.. _speaking];
            }
        }
    }

    /// <summary>Frames sent while <see cref="Ready"/> was false: a real link would have dropped them, so the engine must not.</summary>
    public int SentWhileNotReady => Volatile.Read(ref _sentWhileNotReady);

    public void Send(ReadOnlyMemory<byte> opus)
    {
        if (!_ready)
        {
            Interlocked.Increment(ref _sentWhileNotReady);
        }
        // Copied: the engine reuses its packet buffers.
        var frame = new SentFrame(time.Now, opus.ToArray());
        lock (_gate)
        {
            _sent.Add(frame);
        }
    }

    public void SetSpeaking(bool on)
    {
        var change = new SpeakingChange(time.Now, on);
        lock (_gate)
        {
            _speaking.Add(change);
        }
    }

    public void ClearSent()
    {
        lock (_gate)
        {
            _sent.Clear();
            _speaking.Clear();
        }
    }

    /// <summary>
    /// The user's next 20 ms frame arrives now: one sequence number and 960 samples of timestamp on from
    /// their last, as Discord clients send. Returns the packet raised.
    /// </summary>
    public OpusPacket Deliver(UserId user, ReadOnlyMemory<byte> opus)
    {
        OpusPacket packet;
        lock (_gate)
        {
            var sender = SenderOf(user);
            packet = new OpusPacket(user, sender.Seq++, sender.Ts, opus, time.Now);
            sender.Ts += OpusFormat.FrameSamples;
        }
        PacketReceived(packet);
        return packet;
    }

    /// <summary>
    /// The user stops sending for <paramref name="frames"/> frames and starts again: the timestamp jumps
    /// but the sequence number doesn't, as after a pause in speech.
    /// </summary>
    public void Pause(UserId user, int frames)
    {
        lock (_gate)
        {
            SenderOf(user).Ts += (uint)(frames * OpusFormat.FrameSamples);
        }
    }

    /// <summary>Plays the frames as the user, one every 20 ms by <paramref name="time"/>'s clock, as a talker's client would.</summary>
    public async Task PlayAsync(UserId user, IReadOnlyList<byte[]> frames, CancellationToken cancellationToken = default)
    {
        var start = time.Now;
        for (var i = 0; i < frames.Count; i++)
        {
            var due = start + (TimeSpan.FromMilliseconds(OpusFormat.FrameMs) * i);
            for (var wait = due - time.Now; wait > TimeSpan.Zero; wait = due - time.Now)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
            Deliver(user, frames[i]);
        }
    }

    // Called under the lock.
    private Sender SenderOf(UserId user)
    {
        if (!_senders.TryGetValue(user.V, out var sender))
        {
            // Away from zero, so neither counter's wrap is the first thing a test meets.
            _senders[user.V] = sender = new Sender { Seq = 1000, Ts = 1_000_000 };
        }
        return sender;
    }

    private sealed class Sender
    {
        public ushort Seq { get; set; }

        public uint Ts { get; set; }
    }
}
