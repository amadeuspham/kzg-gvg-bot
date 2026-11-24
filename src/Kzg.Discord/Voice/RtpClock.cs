using Kzg.Core.Abstractions;

namespace Kzg.Discord.Voice;

/// <summary>
/// The sequence number and timestamp a link writes on the packets it sends. The library leaves both to the
/// sender. The sequence number counts packets; the timestamp counts 48 kHz samples, so it moves 960 for
/// every 20 ms of wall time, sent or not. A pause between bursts therefore shows as a jump the receiver's
/// jitter buffer reads as silence, not as one frame following another.
/// </summary>
internal sealed class RtpClock
{
    private const uint FrameTicks = OpusFormat.FrameSamples;

    // Frames closer than this belong to one burst; the engine sends one a tick, 20 ms apart, give or take.
    private static readonly TimeSpan BurstGap = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(OpusFormat.FrameMs);

    private bool _started;
    private ushort _sequence;
    private uint _timestamp;
    private TimeSpan _lastAt;
    private TimeSpan _burstStart;
    private uint _burstTimestamp;

    /// <summary>A random start, as RTP asks, so two links never share a number by accident.</summary>
    public RtpClock(Random? random = null)
    {
        random ??= Random.Shared;
        _sequence = (ushort)random.Next(ushort.MaxValue + 1);
        _timestamp = (uint)random.NextInt64(uint.MaxValue + 1L);
    }

    /// <summary>The numbers for a packet sent at <paramref name="now"/>.</summary>
    public (ushort Sequence, uint Timestamp) Next(TimeSpan now)
    {
        unchecked
        {
            if (!_started)
            {
                _started = true;
                (_burstStart, _burstTimestamp) = (now, _timestamp);
            }
            else
            {
                var gap = now - _lastAt;
                if (gap > BurstGap)
                {
                    // Idle time counts: the timestamp keeps pace with the clock, whole frames at a time.
                    _burstTimestamp = _timestamp + (FrameTicks * (uint)Math.Max(1, Frames(gap)));
                    _burstStart = now;
                    _timestamp = _burstTimestamp;
                }
                else
                {
                    var due = _burstTimestamp + (FrameTicks * (uint)Frames(now - _burstStart));
                    // Never two packets on one timestamp, however fast the engine catches up.
                    _timestamp = (int)(due - _timestamp) >= (int)FrameTicks ? due : _timestamp + FrameTicks;
                    _sequence++;
                    _lastAt = now;
                    return (_sequence, _timestamp);
                }
            }
            _sequence++;
            _lastAt = now;
            return (_sequence, _timestamp);
        }
    }

    private static int Frames(TimeSpan span) => (int)Math.Round(span / Frame);
}
