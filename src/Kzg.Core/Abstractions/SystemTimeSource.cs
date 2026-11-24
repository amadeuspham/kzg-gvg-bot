using System.Diagnostics;

namespace Kzg.Core.Abstractions;

/// <summary>The real monotonic time, from the high-resolution performance counter.</summary>
public sealed class SystemTimeSource : ITimeSource
{
    private readonly long _origin = Stopwatch.GetTimestamp();

    public TimeSpan Now => Stopwatch.GetElapsedTime(_origin);
}
