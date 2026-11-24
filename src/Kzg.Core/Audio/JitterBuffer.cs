using Kzg.Core.Abstractions;

namespace Kzg.Core.Audio;

/// <summary>
/// One player's received audio, held 1.2 s so a relay can start back in time. Packets are placed by
/// their RTP timestamp, so arrival order doesn't matter, and played out a delay behind the earliest
/// they could have come. The relay's read head, <see cref="Read"/>, starts where its trigger says and
/// catches up to live at the next gap in speech. Single-threaded: the engine thread alone calls it.
/// <para>
/// A relay's start and end are times on the receive clock, the one packets and app edges arrive by.
/// A frame counts as arriving when it would have with no jitter, so a late packet never moves the
/// line: a start at a press minus 300 ms takes nothing that arrived earlier.
/// </para>
/// </summary>
public sealed class JitterBuffer
{
    public const int SamplesPerFrame = OpusFormat.FrameSamples;
    public static readonly TimeSpan FrameLength = TimeSpan.FromMilliseconds(OpusFormat.FrameMs);
    public static readonly TimeSpan Capacity = TimeSpan.FromMilliseconds(1200);
    public static readonly TimeSpan MinDelay = TimeSpan.FromMilliseconds(60);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(240);
    public static readonly TimeSpan ShrinkAfter = TimeSpan.FromSeconds(10);

    /// <summary>The most frames in a row Opus conceals; a longer loss plays as silence.</summary>
    public const int MaxConcealed = 3;

    /// <summary>
    /// How much a relay may fall further behind than the least it has lagged this session, as when the
    /// engine stalls, before it jumps to live and drops what it skips.
    /// </summary>
    public static readonly TimeSpan CatchUpAllowance = TimeSpan.FromMilliseconds(60);

    private const int CapacityFrames = 60;

    // A stream that restarts its timestamps this far back is a new stream, not a late packet.
    private const long ResetFrames = CapacityFrames;

    private static readonly int AllowanceFrames = (int)(CatchUpAllowance / FrameLength);

    private readonly SortedList<long, Entry> _frames = [];
    private Cursor? _relay;

    private bool _anchored;
    private uint _newestTsRaw;
    private long _newestTs;
    private ushort _newestSeqRaw;
    private long _newestSeq;
    private TimeSpan _newestArrival;

    // When slot 0 would have arrived with no network jitter: the earliest any packet suggests.
    private TimeSpan _anchor;

    // When the stream's first packet came. Its first frames may have arrived before it by the jitter-free
    // reckoning, if they came in a burst, but no frame of the stream came before it.
    private TimeSpan _streamArrival;

    // The late packets of the last ShrinkAfter, each later than any after it, so the first is the
    // latest: what the delay must still hold.
    private readonly LinkedList<(TimeSpan At, TimeSpan Lag)> _late = [];
    private TimeSpan _grownAt;

    /// <summary>
    /// How far behind the earliest arrival frames play out: 60 ms, grown to the lateness of a packet that
    /// comes too late for it, up to 240 ms. Ten seconds after it last grew, it sizes back down to the
    /// latest packet of the last ten seconds.
    /// </summary>
    public TimeSpan Delay { get; private set; } = MinDelay;

    public bool Relaying => _relay is not null;

    /// <summary>Frames held now, for tests and diagnostics.</summary>
    public int Count => _frames.Count;

    public void Add(OpusPacket packet)
    {
        if (packet.Data.IsEmpty)
        {
            return;
        }
        if (!_anchored)
        {
            Reset(packet);
        }
        ShrinkDelay(packet.Arrival);
        var ts = _newestTs + (int)(packet.RtpTs - _newestTsRaw);
        var seq = _newestSeq + (short)(packet.Seq - _newestSeqRaw);
        if (seq > _newestSeq && ts < _newestTs - (ResetFrames * SamplesPerFrame))
        {
            // Newer by sequence but far older by timestamp: the sender started a fresh stream.
            Reset(packet);
            (ts, seq) = (0, 0);
        }
        var slot = FloorDiv(ts, SamplesPerFrame);
        var lag = packet.Arrival - (_anchor + (slot * FrameLength));
        if (ts > _newestTs)
        {
            // A timestamp jump with no sequence gap is a pause in speech: the next talkspurt re-anchors,
            // so drift between the sender's clock and ours never builds up.
            var pause = seq == _newestSeq + 1 && slot > FloorDiv(_newestTs, SamplesPerFrame) + 1;
            if (pause || packet.Arrival - _newestArrival > Capacity)
            {
                _anchor = packet.Arrival - (slot * FrameLength);
                lag = TimeSpan.Zero;
            }
            (_newestTs, _newestTsRaw, _newestSeq, _newestSeqRaw, _newestArrival) = (ts, packet.RtpTs, seq, packet.Seq, packet.Arrival);
        }
        if (lag < TimeSpan.Zero)
        {
            _anchor += lag;
        }
        else if (lag > MinDelay)
        {
            NoteLate(packet.Arrival, lag);
            if (lag > Delay)
            {
                // Too late for playout: play as far behind as this packet came, for a while.
                GrowDelay(SizeFor(lag), packet.Arrival);
            }
        }
        if (slot <= LiveSlot(packet.Arrival) - CapacityFrames || _frames.ContainsKey(slot))
        {
            return;
        }
        // Copied: the voice library may reuse its buffer once the packet is handed over.
        _frames.Add(slot, new Entry(seq, packet.Data.ToArray()));
        Evict(packet.Arrival);
    }

    /// <summary>
    /// Starts the relay's read head at the first frame that arrived at or after <paramref name="arrivedFrom"/>,
    /// or as far back as the buffer reaches. However far back that is, the relay may lag by it and
    /// still catch up at the next gap; only falling further behind makes it jump.
    /// </summary>
    public void Start(TimeSpan arrivedFrom) => _relay = new Cursor { StartAt = arrivedFrom, LagLimit = long.MaxValue };

    public void Stop() => _relay = null;

    /// <summary>The relay's frame for this tick; silent while it isn't relaying.</summary>
    public FrameRead Read(TimeSpan now)
    {
        if (!_anchored)
        {
            return FrameRead.Silent;
        }
        ShrinkDelay(now);
        return _relay is null ? FrameRead.Silent : ReadAt(_relay, now);
    }

    /// <summary>
    /// Empties the buffer for a new stream, whose numbers have nothing to do with the old one's, and ends a
    /// relay under way. The delay is kept: the network the next stream comes over is the same.
    /// </summary>
    public void Restart()
    {
        _frames.Clear();
        (_anchored, _relay) = (false, null);
    }

    /// <summary>
    /// Whether the relay has read every frame that arrived by <paramref name="arrivedUntil"/>, as a drain
    /// after a key-up needs: true too once there is no relay or no audio to read.
    /// </summary>
    public bool RelayPassed(TimeSpan arrivedUntil) =>
        _relay is not { } relay || !_anchored || (relay.Next is { } next && next > LastArrivedBy(arrivedUntil));

    /// <summary>How far behind live the relay's last frame was; null while it isn't relaying or hasn't read yet.</summary>
    public TimeSpan? RelayLag(TimeSpan now) =>
        _relay is { Next: { } next } && _anchored ? (LiveSlot(now) - next + 1) * FrameLength : null;

    private FrameRead ReadAt(Cursor cursor, TimeSpan now)
    {
        Evict(now);
        var live = LiveSlot(now);
        var oldest = live - CapacityFrames + 1;
        if (cursor.Next is null)
        {
            var from = FirstArrivedFrom(cursor.StartAt);
            if (cursor.StartAt <= _streamArrival && FirstAfter(oldest - 1) is { } first)
            {
                from = Math.Min(from, first);
            }
            cursor.Next = cursor.Floor = Math.Clamp(from, oldest, live + 1);
        }
        // The least lag this session has had bounds how far behind it may fall: a start far back is
        // allowed, a stall that adds to it is not.
        var lag = live - cursor.Next.Value;
        cursor.LagLimit = Math.Min(cursor.LagLimit, Math.Max(lag, 0) + AllowanceFrames);
        if (cursor.Next > live)
        {
            // Ahead of live, as when the delay grew: wait for the frame to be due.
            return FrameRead.Silent;
        }
        if (lag > cursor.LagLimit || cursor.Next < oldest)
        {
            // What it skips is dropped for good: a later rewind stops short of it.
            cursor.Next = cursor.Floor = live;
        }
        // At most three steps: a frame, a jump to live, then a frame or silence there.
        while (true)
        {
            var slot = cursor.Next.Value;
            if (_frames.TryGetValue(slot, out var entry))
            {
                (cursor.Next, cursor.Floor, cursor.Played, cursor.Concealed) = (slot + 1, slot + 1, true, 0);
                cursor.PlayedAt = slot;
                return FrameRead.Frame(entry.Data);
            }
            if (IsLoss(slot))
            {
                cursor.Next = slot + 1;
                if (cursor.Played && cursor.Concealed < MaxConcealed)
                {
                    cursor.Concealed++;
                    return FrameRead.Missing;
                }
                return FrameRead.Silent;
            }
            if (cursor.Played)
            {
                // A gap in speech: nothing is lost by jumping to live here, so a lagging relay catches up.
                (cursor.Played, cursor.Concealed) = (false, 0);
                if (live > slot)
                {
                    cursor.Next = live;
                    continue;
                }
                cursor.Next = slot + 1;
                return FrameRead.Silent;
            }
            // Silence before speech, as a pre-roll that starts early has: skip to the first word.
            var first = FirstAfter(slot);
            if (first is { } f && f <= live)
            {
                cursor.Next = f;
                continue;
            }
            cursor.Next = live + 1;
            return FrameRead.Silent;
        }
    }

    /// <summary>
    /// Whether a missing slot lost a packet, as a sequence gap around it says, rather than fell in a
    /// pause. Past the newest packet it can't be known yet, so a few frames count as lost.
    /// </summary>
    private bool IsLoss(long slot)
    {
        var after = IndexAfter(slot);
        if (after == 0)
        {
            return false;
        }
        var previous = _frames.Keys[after - 1];
        if (after == _frames.Count)
        {
            return slot - previous <= MaxConcealed;
        }
        var lost = _frames.Values[after].Seq - _frames.Values[after - 1].Seq - 1;
        return slot - previous <= lost;
    }

    private long? FirstAfter(long slot)
    {
        var index = IndexAfter(slot);
        return index < _frames.Count ? _frames.Keys[index] : null;
    }

    /// <summary>The index of the first held slot above <paramref name="slot"/>.</summary>
    private int IndexAfter(long slot)
    {
        var keys = _frames.Keys;
        var (low, high) = (0, keys.Count);
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (keys[middle] <= slot)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low;
    }

    /// <summary>
    /// Plays further behind, and takes the relay back over what it passed without a frame since its last
    /// one: the packets that came too late for those slots then play, late, instead of being lost.
    /// </summary>
    private void GrowDelay(TimeSpan delay, TimeSpan at)
    {
        var frames = (delay - Delay).Ticks / FrameLength.Ticks;
        (Delay, _grownAt) = (delay, at);
        if (_relay is { Next: { } next } relay && next > relay.Floor)
        {
            relay.Next = Math.Max(relay.Floor, next - frames);
            (relay.Played, relay.Concealed) = (relay.PlayedAt + 1 == relay.Next, 0);
        }
    }

    private void NoteLate(TimeSpan at, TimeSpan lag)
    {
        while (_late.Last is { } last && last.Value.Lag <= lag)
        {
            _late.RemoveLast();
        }
        _late.AddLast((at, lag));
    }

    private void ShrinkDelay(TimeSpan now)
    {
        while (_late.First is { } first && first.Value.At <= now - ShrinkAfter)
        {
            _late.RemoveFirst();
        }
        if (Delay == MinDelay || now - _grownAt < ShrinkAfter)
        {
            return;
        }
        var delay = SizeFor(_late.First?.Value.Lag ?? TimeSpan.Zero);
        if (delay < Delay)
        {
            // Live moves on by the difference: the relay may fall behind by it too and still catch up at a gap.
            var frames = (Delay - delay).Ticks / FrameLength.Ticks;
            Delay = delay;
            if (_relay is { } relay && relay.LagLimit != long.MaxValue)
            {
                relay.LagLimit += frames;
            }
        }
    }

    /// <summary>The delay that holds a packet <paramref name="lag"/> late: whole frames, from 60 to 240 ms.</summary>
    private static TimeSpan SizeFor(TimeSpan lag)
    {
        var frames = -FloorDiv(-lag.Ticks, FrameLength.Ticks);
        return TimeSpan.FromTicks(Math.Clamp(frames * FrameLength.Ticks, MinDelay.Ticks, MaxDelay.Ticks));
    }

    private void Evict(TimeSpan now)
    {
        var oldest = LiveSlot(now) - CapacityFrames + 1;
        while (_frames.Count > 0 && _frames.Keys[0] < oldest)
        {
            _frames.RemoveAt(0);
        }
    }

    /// <summary>The newest slot due by <paramref name="now"/>.</summary>
    private long LiveSlot(TimeSpan now) => SlotAt(now);

    private long SlotAt(TimeSpan time) => FloorDiv((time - _anchor - Delay).Ticks, FrameLength.Ticks);

    /// <summary>The first slot that arrived, jitter aside, at or after <paramref name="time"/>.</summary>
    private long FirstArrivedFrom(TimeSpan time) => -FloorDiv((_anchor - time).Ticks, FrameLength.Ticks);

    /// <summary>The last slot that arrived, jitter aside, by <paramref name="time"/>.</summary>
    private long LastArrivedBy(TimeSpan time) => FloorDiv((time - _anchor).Ticks, FrameLength.Ticks);

    private void Reset(OpusPacket packet)
    {
        _frames.Clear();
        (_anchored, _anchor, _streamArrival) = (true, packet.Arrival, packet.Arrival);
        (_newestTs, _newestTsRaw, _newestSeq, _newestSeqRaw, _newestArrival) = (0, packet.RtpTs, 0, packet.Seq, packet.Arrival);
        // Slot numbers mean something else now: the relay finds its place again, keeping its leave to lag
        // by where it was placed.
        _relay?.Restart(packet.Arrival, long.MaxValue);
    }

    private static long FloorDiv(long value, long divisor) =>
        (value / divisor) - ((value % divisor != 0 && (value < 0) != (divisor < 0)) ? 1 : 0);

    private sealed record Entry(long Seq, ReadOnlyMemory<byte> Data);

    private sealed class Cursor
    {
        /// <summary>Where the head starts, until it is placed on its first read.</summary>
        public TimeSpan StartAt { get; set; }

        public long? Next { get; set; }

        /// <summary>The lowest slot a rewind may take the head back to: past its last frame, and past what it dropped.</summary>
        public long Floor { get; set; }

        /// <summary>The slot of the last frame it played; long.MinValue before the first.</summary>
        public long PlayedAt { get; set; } = long.MinValue;

        /// <summary>Speech has been read since the last gap, so the next gap is a pause, not leading silence.</summary>
        public bool Played { get; set; }

        public int Concealed { get; set; }

        /// <summary>How far behind live, in frames, the head may fall before it jumps; it only tightens.</summary>
        public long LagLimit { get; set; } = AllowanceFrames;

        public void Restart(TimeSpan at, long lagLimit)
        {
            (StartAt, Next, Played, Concealed, LagLimit) = (at - FrameLength, null, false, 0, lagLimit);
            PlayedAt = long.MinValue;
        }
    }
}
