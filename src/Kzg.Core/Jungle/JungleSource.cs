using Kzg.Core.Abstractions;

namespace Kzg.Core.Jungle;

/// <summary>
/// The jungle calls as an engine source: clips are queued from any thread and read one Opus frame per tick
/// from the engine thread. A queued clip waits for the one playing, so calls never talk over each other.
/// </summary>
/// <param name="clips">Each clip as 20 ms Opus packets, encoded once at startup; every <see cref="JungleClip"/> is needed.</param>
public sealed class JungleSource : IAudioSource
{
    /// <summary>Clips that may wait at once, the one playing included; more are refused.</summary>
    public const int MaxQueued = 4;

    private readonly Lock _gate = new();
    private IReadOnlyDictionary<JungleClip, IReadOnlyList<ReadOnlyMemory<byte>>> _clips;
    private readonly Queue<IReadOnlyList<ReadOnlyMemory<byte>>> _waiting = new();
    private IReadOnlyList<ReadOnlyMemory<byte>>? _playing;
    private int _next;

    public JungleSource(IReadOnlyDictionary<JungleClip, IReadOnlyList<ReadOnlyMemory<byte>>> clips)
    {
        Check(clips);
        _clips = clips;
    }

    /// <summary>
    /// Swaps in re-encoded clips, such as at another volume. A clip already playing or waiting keeps the
    /// packets it was queued with and may finish at the old level; the next one queued uses the new.
    /// </summary>
    public void SetClips(IReadOnlyDictionary<JungleClip, IReadOnlyList<ReadOnlyMemory<byte>>> clips)
    {
        Check(clips);
        lock (_gate)
        {
            _clips = clips;
        }
    }

    private static void Check(IReadOnlyDictionary<JungleClip, IReadOnlyList<ReadOnlyMemory<byte>>> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);
        foreach (var clip in Enum.GetValues<JungleClip>())
        {
            if (!clips.TryGetValue(clip, out var frames) || frames.Count == 0 || frames.Any(f => f.IsEmpty))
            {
                throw new ArgumentException($"clip {clip} needs at least one non-empty Opus frame", nameof(clips));
            }
        }
    }

    public SourceId Id { get; } = SourceId.ForTts("jungle");

    public TargetSpec Targets { get; } = new TargetSpec.Groups([Group.Attack, Group.Defense]);

    /// <summary>Queues a clip. False when <see cref="MaxQueued"/> clips already wait.</summary>
    public bool Play(JungleClip clip)
    {
        lock (_gate)
        {
            if (_waiting.Count + (_playing is null ? 0 : 1) >= MaxQueued)
            {
                return false;
            }

            _waiting.Enqueue(_clips[clip]);
            return true;
        }
    }

    public FrameRead Read(long tick)
    {
        lock (_gate)
        {
            if (_playing is null)
            {
                if (!_waiting.TryDequeue(out _playing))
                {
                    return FrameRead.Silent;
                }

                _next = 0;
            }

            var frame = _playing[_next++];
            if (_next == _playing.Count)
            {
                _playing = null;
            }

            return FrameRead.Frame(frame);
        }
    }
}
