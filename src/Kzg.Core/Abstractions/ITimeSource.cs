namespace Kzg.Core.Abstractions;

/// <summary>
/// Monotonic time: never jumps when the wall clock is set or synced, so timeouts, holds and the
/// audio tick only move with real time. Tests replace it with a fake they advance by hand.
/// </summary>
public interface ITimeSource
{
    /// <summary>Time since an arbitrary fixed origin.</summary>
    TimeSpan Now { get; }
}
