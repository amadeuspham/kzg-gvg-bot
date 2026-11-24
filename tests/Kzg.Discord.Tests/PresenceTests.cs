using Kzg.Core.Abstractions;
using Kzg.Discord.Presence;

namespace Kzg.Discord.Tests;

public sealed class PresenceTests
{
    private const ulong Alice = 1, Bram = 2, Chen = 3, OurBot = 900;
    private const ulong A1 = 101, A2 = 102;

    private readonly List<(UserId User, ChannelId? Channel)> _raised = [];
    private readonly PresenceTracker _tracker;

    public PresenceTests() => _tracker = new PresenceTracker((u, c) => _raised.Add((u, c)), id => id == OurBot);

    [Fact]
    public void The_first_snapshot_raises_everyone_in_voice()
    {
        _tracker.Snapshot([(Alice, A1), (Bram, A2)]);

        Assert.Equal(
            [(new UserId(Alice), (ChannelId?)new ChannelId(A1)), (new UserId(Bram), new ChannelId(A2))],
            _raised.OrderBy(r => r.User.V));
    }

    [Fact]
    public void A_snapshot_after_a_re_identify_raises_only_what_differs()
    {
        _tracker.Snapshot([(Alice, A1), (Bram, A1), (Chen, A2)]);
        _raised.Clear();

        // Alice stayed, Bram moved, Chen left, and Dana (4) joined while the bot was away.
        _tracker.Snapshot([(Alice, A1), (Bram, A2), (4, A1)]);

        Assert.Equal(3, _raised.Count);
        Assert.Contains((new UserId(Chen), (ChannelId?)null), _raised);
        Assert.Contains((new UserId(Bram), (ChannelId?)new ChannelId(A2)), _raised);
        Assert.Contains((new UserId(4), (ChannelId?)new ChannelId(A1)), _raised);
    }

    [Fact]
    public void An_identical_snapshot_raises_nothing()
    {
        _tracker.Snapshot([(Alice, A1)]);
        _raised.Clear();

        _tracker.Snapshot([(Alice, A1)]);

        Assert.Empty(_raised);
    }

    [Fact]
    public void Joins_moves_and_leaves_follow_the_snapshot()
    {
        _tracker.Snapshot([]);

        _tracker.Update(Alice, A1);
        _tracker.Update(Alice, A2);
        _tracker.Update(Alice, null);

        Assert.Equal<(UserId, ChannelId?)[]>(
            [(new UserId(Alice), new ChannelId(A1)), (new UserId(Alice), new ChannelId(A2)), (new UserId(Alice), null)],
            [.. _raised]);
    }

    [Fact]
    public void A_mute_or_deafen_which_moves_nobody_raises_nothing()
    {
        _tracker.Update(Alice, A1);
        _raised.Clear();

        _tracker.Update(Alice, A1);

        Assert.Empty(_raised);
    }

    [Fact]
    public void Leaving_when_nobody_knew_they_were_in_voice_raises_nothing()
    {
        _tracker.Update(Alice, null);

        Assert.Empty(_raised);
    }

    [Fact]
    public void Our_own_bots_are_not_players()
    {
        _tracker.Snapshot([(OurBot, A1), (Alice, A1)]);
        _tracker.Update(OurBot, A2);

        Assert.Equal([new UserId(Alice)], _raised.Select(r => r.User));
        Assert.Equal(1, _tracker.Count);
    }

    [Fact]
    public void A_bot_learned_late_is_dropped_if_it_was_taken_for_a_player()
    {
        var late = new PresenceTracker((u, c) => _raised.Add((u, c)), _ => false);
        late.Update(77, A1);
        _raised.Clear();

        late.Drop(77);

        Assert.Equal([(new UserId(77), (ChannelId?)null)], _raised);
        Assert.Null(late.ChannelOf(77));
    }

    [Fact]
    public void Where_a_user_is_can_be_asked()
    {
        _tracker.Update(Alice, A2);

        Assert.Equal(new ChannelId(A2), _tracker.ChannelOf(Alice));
        Assert.Null(_tracker.ChannelOf(Bram));
    }

    [Fact]
    public void Channel_names_are_set_replaced_and_removed()
    {
        var names = new ChannelNames();
        names.Reset([(101, "Attack 1"), (102, "Attack 2")]);

        names.Set(103, "Attack 3");
        names.Remove(102);
        names.Reset([(101, "Attack One"), (103, "Attack 3")]);

        Assert.Equal("Attack One", names.NameOf(new ChannelId(101)));
        Assert.Null(names.Find(new ChannelId(102)));
        Assert.Equal("Channel 102", names.NameOf(new ChannelId(102)));
        Assert.Equal(2, names.All.Count);
    }
}
