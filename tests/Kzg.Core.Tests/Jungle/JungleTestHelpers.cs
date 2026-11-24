using Kzg.Core.Testing;

namespace Kzg.Core.Tests.Jungle;

internal static class JungleTestHelpers
{
    public static TimeSpan Mmss(int m, int s) => new(0, m, s);

    public static void Advance(this ManualTimeSource time, double seconds) => time.Advance(TimeSpan.FromSeconds(seconds));
}
