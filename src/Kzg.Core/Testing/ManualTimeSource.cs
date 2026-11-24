using Kzg.Core.Abstractions;

namespace Kzg.Core.Testing;

/// <summary>A clock that moves only when told to; safe to read from any thread, as the host reads it from several.</summary>
public sealed class ManualTimeSource : ITimeSource
{
    // Away from zero, so nothing can lean on time starting at the origin.
    private long _ticks = TimeSpan.FromHours(5).Ticks;

    public TimeSpan Now => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
