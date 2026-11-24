using static Kzg.Bot.Tests.Engine.EngineRig;

namespace Kzg.Bot.Tests.Engine;

// A player who moves to another VC, or leaves and comes back, opens a stream of numbers that owe nothing to the old one.
public sealed class EngineStreamTests
{
    [Fact]
    public void A_player_who_moves_mid_broadcast_is_relayed_from_their_new_stream_to_the_VCs_of_the_new_place()
    {
        using var rig = new EngineRig();
        rig.Seat(U1, A1);
        rig.Seat(U2, A2);
        rig.Seat(U3, A3);
        rig.SignIn(U1);
        rig.Idle(1);
        rig.Press(U1, true);
        for (var i = 0; i < 30; i++)
        {
            rig.Step((rig.L1, U1, Packet(1, i)));
        }

        // A2's link sees the player for the first time: its numbers start where any new sender's do, behind where the old stream had got.
        rig.Seat(U1, A2);
        for (var i = 30; i < 60; i++)
        {
            rig.Step((rig.L2, U1, Packet(1, i)));
        }
        rig.Idle(10);

        // The three frames still in the old buffer's playout delay are not carried over to the new one.
        Assert.Equal([.. Enumerable.Range(0, 27), .. Enumerable.Range(30, 30)], Indexes(rig.L3));
        // A1 heard nothing until the move, then the new stream whole: from its first frame, with no gap.
        Assert.Equal([.. Enumerable.Range(30, 30)], Indexes(rig.L1));
        // A2 heard the first half, while the player was elsewhere, and nothing of their own VC after.
        Assert.Equal([.. Enumerable.Range(0, 27)], Indexes(rig.L2));
        Assert.Empty(rig.LD.Sent);
    }
}

// A client may number its packets either way across a pause: the timestamp keeps pace with the clock, or goes on from the last number.
public sealed class EngineTalkSpurtTests
{
    private static List<int> Relay(EngineRig rig, bool timestampFollowsClock, int pauseTicks, bool keyHeldThroughPause)
    {
        rig.Seat(U1, A1);
        rig.Seat(U2, A2);
        rig.SignIn(U1);
        rig.Idle(1);
        rig.Press(U1, true);
        for (var i = 0; i < 30; i++)
        {
            rig.Step((rig.L1, U1, Packet(1, i)));
        }
        if (!keyHeldThroughPause)
        {
            rig.Press(U1, false);
        }
        for (var t = 0; t < pauseTicks; t++)
        {
            if (t % 50 == 0)
            {
                rig.Beat(U1, keyHeldThroughPause);
            }
            rig.Step();
        }
        rig.L2.ClearSent();
        if (timestampFollowsClock)
        {
            rig.L1.Pause(U1, pauseTicks);
        }
        if (!keyHeldThroughPause)
        {
            rig.Press(U1, true);
        }
        for (var i = 100; i < 130; i++)
        {
            rig.Step((rig.L1, U1, Packet(1, i)));
        }
        rig.Idle(10);
        return [.. EngineRig.Indexes(rig.L2)];
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_second_broadcast_after_a_long_pause_is_relayed_whole_whichever_way_the_client_numbers_it(bool timestampFollowsClock)
    {
        using var rig = new EngineRig();

        var relayed = Relay(rig, timestampFollowsClock, pauseTicks: 200, keyHeldThroughPause: false);

        Assert.Equal([.. Enumerable.Range(100, 30)], relayed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_key_held_through_the_pause_relays_what_is_said_after_it_whichever_way_the_client_numbers_it(bool timestampFollowsClock)
    {
        using var rig = new EngineRig();

        var relayed = Relay(rig, timestampFollowsClock, pauseTicks: 200, keyHeldThroughPause: true);

        Assert.Equal([.. Enumerable.Range(100, 30)], relayed);
    }

    [Fact]
    public void A_short_gap_in_a_continuous_stream_does_not_restart_the_buffer()
    {
        using var rig = new EngineRig();
        rig.Seat(U1, A1);
        rig.Seat(U2, A2);
        rig.SignIn(U1);
        rig.Idle(1);
        rig.Press(U1, true);
        for (var i = 0; i < 30; i++)
        {
            rig.Step((rig.L1, U1, Packet(1, i)));
        }
        // 200 ms of network stall, then the player's next frames: one continuous stream.
        rig.Idle(10);
        rig.L1.Pause(U1, 10);
        for (var i = 30; i < 60; i++)
        {
            rig.Step((rig.L1, U1, Packet(1, i)));
        }
        rig.Idle(10);

        var relayed = EngineRig.Indexes(rig.L2);

        Assert.Equal([.. Enumerable.Range(0, 60)], relayed);
    }
}
