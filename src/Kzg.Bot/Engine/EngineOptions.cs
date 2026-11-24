using Kzg.Core.Abstractions;

namespace Kzg.Bot.Engine;

/// <summary>How the engine thread paces itself and tidies up. The defaults are the plan's.</summary>
public sealed record EngineOptions
{
    /// <summary>
    /// How far ahead of its 20 ms slot each tick runs: a tick's frames go out this early, as Gengfu's
    /// <c>VoiceSender</c> does, so a coarse sleep still keeps listeners' buffers fed.
    /// </summary>
    public TimeSpan Lead { get; init; } = TimeSpan.FromMilliseconds(40);

    /// <summary>How far behind a tick may run before the engine stops catching up and takes the next slot from now.</summary>
    public TimeSpan MaxBehind { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Ticks without a frame before a link's speaking flag drops.</summary>
    public int SpeakingHangoverTicks { get; init; } = 5;

    /// <summary>How long a player may send nothing before their buffer is dropped, unless they are relaying.</summary>
    public TimeSpan IdleUserAfter { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many received packets may wait for the engine thread. Past it, packets are dropped and counted:
    /// a stalled engine must not grow memory without end.
    /// </summary>
    public int MaxQueuedPackets { get; init; } = 20_000;

    /// <summary>
    /// How much longer than its timestamp says a packet may come after the one before it before it is taken
    /// for the start of a new talk-spurt. A client whose timestamp follows its clock through a pause shows no
    /// such gap; one that goes on from its last number does, and its packets would otherwise sit far behind
    /// the buffer's live position and never play.
    /// </summary>
    public TimeSpan TalkSpurtGap { get; init; } = TimeSpan.FromMilliseconds(500);

    public static TimeSpan FrameLength { get; } = TimeSpan.FromMilliseconds(OpusFormat.FrameMs);
}

/// <summary>What the engine counts, for tests and diagnostics.</summary>
public sealed record EngineStats(
    long Ticks,
    long FramesSent,
    long PacketsDropped,
    long DecodeErrors,
    long SendErrors,
    long TickFaults,
    long Resyncs,
    long Concealed = 0,
    long SourceErrors = 0);
