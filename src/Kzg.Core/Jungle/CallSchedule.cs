namespace Kzg.Core.Jungle;

public enum JungleClip { Jungle40, Jungle20, JungleHalftime40, JungleHalftime20 }

/// <param name="Spawn">Game time of the spawn.</param>
/// <param name="Lead">How long before the spawn the call plays.</param>
/// <param name="At">Game time the call plays: Spawn + Lead.</param>
public sealed record JungleCall(TimeSpan Spawn, TimeSpan Lead, TimeSpan At, JungleClip Clip);

/// <summary>
/// The 10 calls of a match. A call fires once, only within <see cref="Grace"/> after its time:
/// a late "in 40 seconds" is wrong, so a sync that jumps past a call skips it.
/// A sync back above a call's time re-arms it.
/// </summary>
public sealed class CallSchedule
{
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Halftime = TimeSpan.FromMinutes(20);

    private static readonly int[] SpawnMinutes = [25, 20, 15, 10, 5];
    private static readonly int[] LeadSeconds = [40, 20];

    private readonly MatchClock _clock;
    private readonly bool[] _done;

    public CallSchedule(MatchClock clock)
    {
        _clock = clock;
        Calls = [.. SpawnMinutes.SelectMany(_ => LeadSeconds, MakeCall)];
        _done = new bool[Calls.Count];
    }

    public IReadOnlyList<JungleCall> Calls { get; }

    /// <summary>The first call still ahead of the game time, or null when none is left.</summary>
    public JungleCall? Next
    {
        get
        {
            var now = _clock.GameTime;
            return _clock.State is ClockState.Idle or ClockState.Running or ClockState.Paused
                ? Calls.FirstOrDefault(c => c.At < now)
                : null;
        }
    }

    /// <summary>Calls that are due now, each returned once. Nothing is due unless the clock runs.</summary>
    public IReadOnlyList<JungleCall> TakeDue()
    {
        var now = _clock.GameTime;
        if (_clock.State != ClockState.Running)
        {
            return [];
        }

        var due = new List<JungleCall>();
        for (var i = 0; i < Calls.Count; i++)
        {
            var at = Calls[i].At;
            if (now > at)
            {
                _done[i] = false;
            }
            else if (!_done[i])
            {
                _done[i] = true;
                if (now > at - Grace)
                {
                    due.Add(Calls[i]);
                }
            }
        }

        return due;
    }

    private static JungleCall MakeCall(int spawnMinutes, int leadSeconds)
    {
        var spawn = TimeSpan.FromMinutes(spawnMinutes);
        var lead = TimeSpan.FromSeconds(leadSeconds);
        var halftime = spawn == Halftime;
        var clip = (halftime, leadSeconds) switch
        {
            (false, 40) => JungleClip.Jungle40,
            (false, _) => JungleClip.Jungle20,
            (true, 40) => JungleClip.JungleHalftime40,
            (true, _) => JungleClip.JungleHalftime20,
        };
        return new JungleCall(spawn, lead, spawn + lead, clip);
    }
}
