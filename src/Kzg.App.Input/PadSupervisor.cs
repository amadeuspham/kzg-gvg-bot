using Kzg.Core.Abstractions;

namespace Kzg.App.Input;

/// <summary>What to do after the controller helper exits on its own.</summary>
public abstract record PadDecision
{
    private PadDecision()
    {
    }

    public sealed record Restart(TimeSpan After) : PadDecision;

    public sealed record GiveUp : PadDecision;
}

/// <summary>
/// Restart policy for the controller helper: a quick, doubling pause between restarts, and a stop after
/// <see cref="MaxQuickFailures"/> helpers in a row that died within <see cref="StableAfter"/> of starting.
/// A helper that ran longer than that counts as healthy, so one crash a day never adds up to a give-up.
/// </summary>
public sealed class PadSupervisor(ITimeSource time)
{
    public const int MaxQuickFailures = 5;

    public static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LongestDelay = TimeSpan.FromSeconds(8);

    private TimeSpan _startedAt;
    private int _quickFailures;

    public void Started() => _startedAt = time.Now;

    public PadDecision Exited()
    {
        if (time.Now - _startedAt >= StableAfter)
        {
            _quickFailures = 0;
        }
        _quickFailures++;
        if (_quickFailures >= MaxQuickFailures)
        {
            return new PadDecision.GiveUp();
        }
        var delay = FirstDelay * Math.Pow(2, _quickFailures - 1);
        return new PadDecision.Restart(delay > LongestDelay ? LongestDelay : delay);
    }

    /// <summary>The user asked to try again after a give-up.</summary>
    public void Reset() => _quickFailures = 0;
}
