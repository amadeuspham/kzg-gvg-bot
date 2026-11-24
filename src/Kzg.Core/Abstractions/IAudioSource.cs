namespace Kzg.Core.Abstractions;

/// <summary>
/// Audio that is not a player's own voice, such as phase 2's jungle calls, read one 20 ms frame per
/// engine tick.
/// </summary>
public interface IAudioSource
{
    SourceId Id { get; }

    TargetSpec Targets { get; }

    FrameRead Read(long tick);
}

/// <summary>Which VCs a source reaches. Routing resolves it to bound VCs on every tick.</summary>
public abstract record TargetSpec
{
    private TargetSpec()
    {
    }

    /// <summary>The VCs these users are in, wherever they move.</summary>
    public sealed record Users(IReadOnlyList<UserId> Ids) : TargetSpec;

    /// <summary>Every bound VC of these groups.</summary>
    public sealed record Groups(IReadOnlyList<Group> Names) : TargetSpec;

    /// <summary>These VCs, if bound.</summary>
    public sealed record Channels(IReadOnlyList<ChannelId> Ids) : TargetSpec;
}

public enum FrameKind
{
    /// <summary>Nothing to play this tick.</summary>
    Silent,

    /// <summary>An Opus frame in <see cref="FrameRead.Opus"/>.</summary>
    Frame,

    /// <summary>A frame was due and never came: the decoder conceals it.</summary>
    Missing,
}

/// <summary>What one tick of a source gave.</summary>
public readonly record struct FrameRead(FrameKind Kind, ReadOnlyMemory<byte> Opus)
{
    public static FrameRead Silent => default;

    public static FrameRead Missing => new(FrameKind.Missing, ReadOnlyMemory<byte>.Empty);

    public static FrameRead Frame(ReadOnlyMemory<byte> opus) =>
        opus.IsEmpty ? throw new ArgumentException("a frame holds an Opus packet", nameof(opus))
        : new(FrameKind.Frame, opus);
}
