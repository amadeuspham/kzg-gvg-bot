using Kzg.Core.Abstractions;
using static Kzg.Bot.Tests.Engine.EngineRig;

namespace Kzg.Bot.Tests.Engine;

// Where a held key's relay starts and ends, in frames by when they arrived. Frame i arrives at
// step i, so a press or release just before step i happens as frame i arrives.
public sealed class EngineStartAndTailTests
{
    private static EngineRig Team()
    {
        var rig = new EngineRig();
        rig.Seat(U1, A1);
        rig.Seat(U2, A2);
        rig.SignIn(U1);
        rig.Idle(1);
        return rig;
    }

    /// <summary>U1 talks without a break for <paramref name="frames"/> frames, pressing and releasing as told.</summary>
    private static int[] Talk(EngineRig rig, int frames, int pressAt, int releaseAt)
    {
        for (var i = 0; i < frames; i++)
        {
            if (i == pressAt)
            {
                rig.Press(U1, true);
            }
            if (i == releaseAt)
            {
                rig.Press(U1, false);
            }
            rig.Step((rig.L1, U1, Packet(1, i)));
        }
        rig.Idle(40);
        return Indexes(rig.L2);
    }

    [Fact]
    public void A_press_reaches_back_300_ms_and_no_further()
    {
        using var rig = Team();

        var relayed = Talk(rig, 80, pressAt: 40, releaseAt: 70);

        // 300 ms before frame 40 arrived, frame 25 did: the small team's talk before that stays theirs.
        Assert.Equal(25, relayed[0]);
        Assert.Equal(Enumerable.Range(25, relayed.Length), relayed);
    }

    [Fact]
    public void A_release_keeps_the_tail_the_relay_was_still_behind_on()
    {
        using var rig = Team();

        var relayed = Talk(rig, 80, pressAt: 10, releaseAt: 50);

        // Everything that arrived up to the release goes out, frame 50 last, and nothing said after it.
        Assert.Equal(Enumerable.Range(0, 51), relayed);
    }

    [Fact]
    public void Pressing_again_while_the_tail_drains_carries_on_unbroken()
    {
        using var rig = Team();

        for (var i = 0; i < 80; i++)
        {
            if (i == 10 || i == 52)
            {
                rig.Press(U1, true);
            }
            if (i == 50 || i == 70)
            {
                rig.Press(U1, false);
            }
            rig.Step((rig.L1, U1, Packet(1, i)));
        }
        rig.Idle(40);

        // Released at 50, pressed again at 52 before the tail ran out: one relay, no gap, no repeat.
        Assert.Equal(Enumerable.Range(0, 71), Indexes(rig.L2));
    }

    [Fact]
    public void A_release_a_heartbeat_reports_stops_at_once_because_its_time_is_a_guess()
    {
        using var rig = Team();

        for (var i = 0; i < 80; i++)
        {
            if (i == 10)
            {
                rig.Press(U1, true);
            }
            if (i == 50)
            {
                rig.Beat(U1, held: false);
            }
            rig.Step((rig.L1, U1, Packet(1, i)));
        }
        rig.Idle(40);

        var relayed = Indexes(rig.L2);
        Assert.Equal(0, relayed[0]);
        Assert.InRange(relayed[^1], 30, 49);
    }

    [Fact]
    public void After_the_tail_the_speaking_flag_drops_and_the_buffer_stops()
    {
        using var rig = Team();

        Talk(rig, 80, pressAt: 10, releaseAt: 50);

        Assert.False(rig.L2.SpeakingChanges[^1].On);
        var sent = rig.L2.Sent.Count;
        rig.Idle(20);
        Assert.Equal(sent, rig.L2.Sent.Count);
    }
}
