using Kzg.Core.Abstractions;

namespace Kzg.Core.Tests.Abstractions;

public class IAudioSourceTests
{
    [Fact]
    public void A_frame_read_is_silent_missing_or_a_frame()
    {
        Assert.Equal(FrameKind.Silent, FrameRead.Silent.Kind);
        Assert.Equal(FrameKind.Silent, default(FrameRead).Kind);
        Assert.Equal(FrameKind.Missing, FrameRead.Missing.Kind);
        Assert.True(FrameRead.Missing.Opus.IsEmpty);

        var frame = FrameRead.Frame(new byte[] { 1, 2, 3 });
        Assert.Equal(FrameKind.Frame, frame.Kind);
        Assert.Equal(new byte[] { 1, 2, 3 }, frame.Opus.ToArray());
    }

    [Fact]
    public void A_frame_holds_a_packet()
    {
        Assert.Throws<ArgumentException>(() => FrameRead.Frame(ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void Target_specs_name_users_groups_or_channels()
    {
        TargetSpec[] specs =
        [
            new TargetSpec.Users([new UserId(1)]),
            new TargetSpec.Groups([Group.Attack, Group.Defense]),
            new TargetSpec.Channels([new ChannelId(9)]),
        ];

        var described = specs.Select(s => s switch
        {
            TargetSpec.Users u => $"users {u.Ids.Count}",
            TargetSpec.Groups g => $"groups {g.Names.Count}",
            TargetSpec.Channels c => $"channels {c.Ids.Count}",
            _ => "unknown",
        });

        Assert.Equal(["users 1", "groups 2", "channels 1"], described);
    }
}
