using Kzg.Core.Abstractions;
using Kzg.Core.Jungle;

namespace Kzg.Core.Tests.Jungle;

public sealed class JungleSourceTests
{
    // Clip n has n + 1 frames, and every frame's first byte is the clip number, so a read names its clip.
    private static JungleSource Source() => new(Enum.GetValues<JungleClip>().ToDictionary(
        clip => clip,
        clip => (IReadOnlyList<ReadOnlyMemory<byte>>)[.. Enumerable.Range(0, (int)clip + 1)
            .Select(i => (ReadOnlyMemory<byte>)new byte[] { (byte)clip, (byte)i })]));

    private static (byte Clip, byte Index) Read(JungleSource source, long tick = 0)
    {
        var read = source.Read(tick);
        Assert.Equal(FrameKind.Frame, read.Kind);
        return (read.Opus.Span[0], read.Opus.Span[1]);
    }

    [Fact]
    public void Reaches_both_groups_and_names_itself()
    {
        var source = Source();
        Assert.Equal(SourceId.ForTts("jungle"), source.Id);
        Assert.Equal([Group.Attack, Group.Defense], Assert.IsType<TargetSpec.Groups>(source.Targets).Names);
    }

    [Fact]
    public void Is_silent_until_a_clip_is_queued()
    {
        Assert.Equal(FrameKind.Silent, Source().Read(0).Kind);
    }

    [Fact]
    public void Plays_a_clip_frame_by_frame_then_goes_silent()
    {
        var source = Source();
        Assert.True(source.Play(JungleClip.JungleHalftime40));
        Assert.Equal(((byte)2, (byte)0), Read(source));
        Assert.Equal(((byte)2, (byte)1), Read(source));
        Assert.Equal(((byte)2, (byte)2), Read(source));
        Assert.Equal(FrameKind.Silent, source.Read(3).Kind);
        Assert.Equal(FrameKind.Silent, source.Read(4).Kind);
    }

    [Fact]
    public void Queued_clips_play_in_order_without_a_gap()
    {
        var source = Source();
        Assert.True(source.Play(JungleClip.JungleHalftime40));
        Assert.True(source.Play(JungleClip.Jungle40));
        Assert.True(source.Play(JungleClip.Jungle20));

        Assert.Equal([(2, 0), (2, 1), (2, 2), (0, 0), (1, 0), (1, 1)],
            Enumerable.Range(0, 6).Select(i => Read(source, i)).Select(r => ((int)r.Clip, (int)r.Index)));
        Assert.Equal(FrameKind.Silent, source.Read(6).Kind);
    }

    [Fact]
    public void Refuses_a_clip_when_MaxQueued_wait()
    {
        var source = Source();
        for (var i = 0; i < JungleSource.MaxQueued; i++)
        {
            Assert.True(source.Play(JungleClip.Jungle40));
        }

        Assert.False(source.Play(JungleClip.Jungle40));
    }

    [Fact]
    public void The_clip_playing_counts_toward_MaxQueued()
    {
        var source = Source();
        for (var i = 0; i < JungleSource.MaxQueued; i++)
        {
            Assert.True(source.Play(JungleClip.Jungle20));
        }

        Read(source); // starts the first clip; it still counts until its last frame
        Assert.False(source.Play(JungleClip.Jungle20));

        Read(source);
        Assert.True(source.Play(JungleClip.Jungle20)); // the first clip has ended
    }

    [Fact]
    public void Accepts_clips_again_after_the_queue_drains()
    {
        var source = Source();
        for (var i = 0; i < JungleSource.MaxQueued; i++)
        {
            Assert.True(source.Play(JungleClip.Jungle40));
        }

        for (var i = 0; i < JungleSource.MaxQueued; i++)
        {
            Read(source);
        }

        Assert.Equal(FrameKind.Silent, source.Read(9).Kind);
        Assert.True(source.Play(JungleClip.Jungle40));
    }

    [Fact]
    public void A_missing_or_empty_clip_is_refused_at_construction()
    {
        var clips = Enum.GetValues<JungleClip>().ToDictionary(
            clip => clip, clip => (IReadOnlyList<ReadOnlyMemory<byte>>)[new byte[] { 1 }]);

        clips.Remove(JungleClip.Jungle20);
        Assert.Throws<ArgumentException>(() => new JungleSource(clips));

        clips[JungleClip.Jungle20] = [];
        Assert.Throws<ArgumentException>(() => new JungleSource(clips));

        clips[JungleClip.Jungle20] = [ReadOnlyMemory<byte>.Empty];
        Assert.Throws<ArgumentException>(() => new JungleSource(clips));
    }

    [Fact]
    public async Task Play_and_Read_may_run_on_different_threads()
    {
        var source = Source();
        var play = Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                source.Play(JungleClip.Jungle20);
            }
        });
        var read = Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                source.Read(i);
            }
        });
        await Task.WhenAll(play, read);
    }

    private static IReadOnlyDictionary<JungleClip, IReadOnlyList<ReadOnlyMemory<byte>>> Marked(byte mark) =>
        Enum.GetValues<JungleClip>().ToDictionary(
            clip => clip,
            clip => (IReadOnlyList<ReadOnlyMemory<byte>>)[.. Enumerable.Range(0, (int)clip + 1).Select(i => (ReadOnlyMemory<byte>)new byte[] { mark, (byte)i })]);

    [Fact]
    public void Swapped_clips_serve_the_next_call_while_the_one_playing_finishes_as_it_was()
    {
        var source = new JungleSource(Marked(1));
        Assert.True(source.Play(JungleClip.JungleHalftime40));
        Assert.Equal(((byte)1, (byte)0), Read(source));
        Assert.Equal(((byte)1, (byte)1), Read(source, 1));

        source.SetClips(Marked(2));
        Assert.True(source.Play(JungleClip.Jungle20));

        var rest = Read(source, 2);
        Assert.Equal(((byte)1, (byte)2), rest);
        Assert.Equal(((byte)2, (byte)0), Read(source, 3));
        Assert.Equal(FrameKind.Frame, source.Read(4).Kind);
        Assert.Equal(FrameKind.Silent, source.Read(5).Kind);
    }

    [Fact]
    public void Swapping_in_an_incomplete_set_is_refused_and_the_old_clips_stay()
    {
        var source = new JungleSource(Marked(1));
        var incomplete = Marked(2).Where(c => c.Key != JungleClip.Jungle20).ToDictionary(c => c.Key, c => c.Value);

        Assert.Throws<ArgumentException>(() => source.SetClips(incomplete));

        Assert.True(source.Play(JungleClip.Jungle20));
        Assert.Equal((byte)1, Read(source).Clip);
    }
}
